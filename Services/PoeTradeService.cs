using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// POE2 市集交易接口服务，支持国服与国际服切换。
/// 国服：https://poe.game.qq.com/api/trade2
/// 国际服：https://www.pathofexile.com/api/trade2
/// </summary>
public class PoeTradeService
{
    private const string ChinaBaseUrl = "https://poe.game.qq.com/api/trade2";
    private const string IntlBaseUrl = "https://www.pathofexile.com/api/trade2";
    private const int MaxFetchIds = 10;
    // 参考 xiletrade-master：不使用固定长延迟，只在 API 限流时等待。
    // 保留小幅延迟避免触发国服 API 限流（xiletrade 用响应头驱动，这里简化为固定低延迟）。
    private const int RequestDelayMs = 200;
    // 收到 429 时的默认退避秒数（若响应无 Retry-After 头）。
    private const int RateLimitBackoffSec = 5;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);

    /// <summary>当前使用的交易 API 根地址。</summary>
    public string BaseUrl { get; set; }

    /// <summary>是否为国服。</summary>
    public bool IsChina { get; set; } = true;

    // stats 缓存：扁平列表，存储原始 stat 文本（含 # 占位符）和对应的 ID/分类。
    // 参考 xiletrade-master 的 FilterData 结构，不做预归一化，保留原始文本用于正则匹配。
    private List<StatEntry>? _statsList;
    private DateTime _statsCacheTime;
    private static readonly TimeSpan StatsCacheExpiry = TimeSpan.FromHours(6);

    /// <summary>stat 缓存条目。</summary>
    private record StatEntry(string Text, string Id, string Category)
    {
        public int Distance { get; init; }
    }

    // 正则：匹配词缀中的数值（含小数和负号），用于替换为 # 占位符。
    private static readonly Regex DecimalPattern = new(@"[-]?[0-9]+\.?[0-9]*|[-]?[0-9]+\.[0-9]+", RegexOptions.Compiled);
    // 正则：匹配词缀中的括号范围 (min-max) 或 (val)，用于剥离 tier 信息。
    private static readonly Regex TierRangeRegex = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    // 正则：匹配 # 或 \#（转义后），用于替换为数字匹配模式。
    private static readonly Regex EscapedDiezePattern = new(@"\\#", RegexOptions.Compiled);
    // stat 匹配中 # 的替换模式：匹配数字或 # 占位符（同时兼容游戏文本和 API stat 文本）。
    private const string DecimalPatternDieze = @"[+-]?([0-9]+\.[0-9]+|[0-9]+|#)";
    // 正则：匹配括号范围之前的第一个数值（含小数和负号），用于精确搜索。
    private static readonly Regex ModValueRegex = new(@"(-?\d+\.?\d*)\s*\(", RegexOptions.Compiled);

    public PoeTradeService(HttpClient httpClient, bool isChina = true)
    {
        _httpClient = httpClient;
        IsChina = isChina;
        BaseUrl = isChina ? ChinaBaseUrl : IntlBaseUrl;
    }

    /// <summary>
    /// 从交易 API 获取可用赛季列表。参考 xiletrade-master 的 DataUpdaterService.LeaguesUpdate。
    /// 赛季列表端点不需要 POESESSID 认证。
    /// </summary>
    public async Task<List<string>> GetLeaguesAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/data/leagues";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommonHeaders(request, null);

        await _rateLimiter.WaitAsync(ct);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"获取赛季列表失败：{(int)response.StatusCode} {json}");
                return [];
            }

            var leagues = new List<string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var league in resultArr.EnumerateArray())
                {
                    var id = league.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id))
                    {
                        leagues.Add(id);
                    }
                }
            }

            AppLogger.Instance.Info($"获取赛季列表：{leagues.Count} 个赛季：{string.Join(", ", leagues)}");
            return leagues;
        }
        finally
        {
            await Task.Delay(RequestDelayMs, ct);
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// 按物品名称/基底搜索，返回搜索结果摘要。
    /// searchByType=true 时按基底(type)查询，否则按名称(name)查询（适用于传奇物品）。
    /// baseType 不为空且 searchByType=false 时，同时传入 type 字段（参考 xiletrade-master：传奇物品同时传 name 和 type）。
    /// itemLevel 不为 null 时添加物品等级筛选。
    /// rarity 不为空时添加稀有度筛选。
    /// selectedMods 不为空时按词缀过滤：每个元素 = (词缀文本, 词缀类型)，词缀类型用于映射到正确的 stat 分类（implicit/explicit/crafted）。
    /// isExactSearch=true 时按词缀的具体数值过滤。
    /// quality/corrupted/identified 参考 xiletrade-master GetTypeFilters/GetMiscFilters。
    /// </summary>
    public async Task<TradeSearchResult> SearchAsync(
        string league,
        string itemName,
        string? sessionId,
        bool searchByType = true,
        string? baseType = null,
        int? itemLevel = null,
        string? rarity = null,
        List<(string text, string type)>? selectedMods = null,
        bool isExactSearch = false,
        int? quality = null,
        bool? corrupted = null,
        bool? identified = null,
        int? armour = null,
        int? evasion = null,
        int? energyShield = null,
        int? dpsTotal = null,
        int? dpsPhys = null,
        int? dpsElem = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            throw new ArgumentException("请先在设置页配置查价器目标赛季。", nameof(league));
        }

        if (string.IsNullOrWhiteSpace(itemName))
        {
            return new TradeSearchResult();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("未配置 POESESSID，请在设置页点击「浏览器登录获取」或手动填写后再使用查价器。");
        }

        // 如果有选中词缀，先匹配 stat ID 和解析数值。
        List<(string id, double? value)>? matchedStats = null;
        if (selectedMods != null && selectedMods.Count > 0)
        {
            matchedStats = await MatchModsToStatIdsAsync(selectedMods, sessionId, cancellationToken);
            AppLogger.Instance.Info($"词缀匹配结果：{selectedMods.Count} 个词缀，匹配到 {matchedStats.Count} 个 stat ID");
        }

        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var url = $"{BaseUrl}/search/{Uri.EscapeDataString(league)}";

            // 构建 type_filters.filters：稀有度筛选。
            // 参考 xiletrade-master GetTypeFilters：rarity 设置时 Disabled=false。
            var typeFiltersInner = new Dictionary<string, object>();
            var typeFiltersDisabled = true;
            if (!string.IsNullOrWhiteSpace(rarity))
            {
                var rarityOption = rarity switch
                {
                    "普通" or "Normal" => "normal",
                    "魔法" or "Magic" => "magic",
                    "稀有" or "Rare" => "rare",
                    "传奇" or "Unique" => "unique",
                    _ => null
                };
                if (rarityOption != null)
                {
                    typeFiltersInner["rarity"] = new { option = rarityOption };
                    typeFiltersDisabled = false;
                }
            }
            if (itemLevel.HasValue)
            {
                typeFiltersInner["ilvl"] = new { min = itemLevel.Value };
                typeFiltersDisabled = false;
            }
            // 品质（参考 xiletrade type_filters.filters.quality）。
            if (quality.HasValue)
            {
                typeFiltersInner["quality"] = new { min = quality.Value };
                typeFiltersDisabled = false;
            }

            // POE2 API 要求 type_filters/misc_filters/trade_filters 内层嵌套 filters 对象。
            // 参考 xiletrade-master FiltersTwo/TypeTwo/TradeTwo 结构。
            var filtersDict = new Dictionary<string, object>();
            if (typeFiltersInner.Count > 0)
            {
                filtersDict["type_filters"] = new { disabled = typeFiltersDisabled, filters = typeFiltersInner };
            }

            // misc_filters：corrupted/identified（参考 xiletrade-master GetMiscFilters）。
            var miscFiltersInner = new Dictionary<string, object>();
            if (corrupted.HasValue)
            {
                miscFiltersInner["corrupted"] = new { option = corrupted.Value ? "true" : "false" };
            }
            if (identified.HasValue)
            {
                miscFiltersInner["identified"] = new { option = identified.Value ? "true" : "false" };
            }
            if (miscFiltersInner.Count > 0)
            {
                filtersDict["misc_filters"] = new { disabled = false, filters = miscFiltersInner };
            }

            // equipment_filters：ar/es/ev/dps/pdps/edps（参考 xiletrade-master GetEquipmentFilters）。
            // JSON 字段名 ar=护甲, es=能量护盾, ev=闪避值, dps=总DPS, pdps=物理DPS, edps=元素DPS。
            var equipmentFiltersInner = new Dictionary<string, object>();
            if (armour.HasValue)
            {
                equipmentFiltersInner["ar"] = new { min = armour.Value };
            }
            if (evasion.HasValue)
            {
                equipmentFiltersInner["ev"] = new { min = evasion.Value };
            }
            if (energyShield.HasValue)
            {
                equipmentFiltersInner["es"] = new { min = energyShield.Value };
            }
            if (dpsTotal.HasValue)
            {
                equipmentFiltersInner["dps"] = new { min = dpsTotal.Value };
            }
            if (dpsPhys.HasValue)
            {
                equipmentFiltersInner["pdps"] = new { min = dpsPhys.Value };
            }
            if (dpsElem.HasValue)
            {
                equipmentFiltersInner["edps"] = new { min = dpsElem.Value };
            }
            if (equipmentFiltersInner.Count > 0)
            {
                filtersDict["equipment_filters"] = new { disabled = false, filters = equipmentFiltersInner };
            }

            // trade_filters：sale_type="priced" 只返回已标价挂单，避免大量未标价条目干扰。
            // 参考 xiletrade-master GetTradeFilters(useSaleType=true)。
            filtersDict["trade_filters"] = new
            {
                disabled = false,
                filters = new { sale_type = new { option = "priced" } }
            };

            object filtersObj = filtersDict;

            // 构建 stats 过滤器。
            object? statsObj = null;
            if (matchedStats != null && matchedStats.Count > 0)
            {
                var statFilters = matchedStats.Select(stat =>
                {
                    if (isExactSearch && stat.value.HasValue)
                    {
                        return (object)new { id = stat.id, value = new { min = stat.value.Value, max = stat.value.Value } };
                    }
                    return (object)new { id = stat.id };
                }).ToArray();
                statsObj = new[]
                {
                    new { type = "and", filters = statFilters }
                };
            }

            // 使用 Dictionary 灵活构建 query，因为 stats/filters 可能不存在。
            // POE2 API 要求 status 字段指定市场状态，否则返回 400 "Invalid query"。
            // status="any" 包含在线和离线挂单（比 "online" 更宽松），参考 xiletrade-master 默认 "available"。
            var queryDict = new Dictionary<string, object?>
            {
                ["sort"] = new { price = "asc" },
            };

            var queryInner = new Dictionary<string, object?>
            {
                ["status"] = new { option = "any" },
            };
            if (searchByType)
            {
                queryInner["type"] = itemName;
            }
            else
            {
                queryInner["name"] = itemName;
                // 传奇物品同时传 name 和 type（参考 xiletrade-master JsonDataTwoFactory.Create：unique 时同时设置 Name 和 Type）。
                if (!string.IsNullOrWhiteSpace(baseType))
                {
                    queryInner["type"] = baseType;
                }
            }
            queryInner["filters"] = filtersObj;
            if (statsObj != null)
            {
                queryInner["stats"] = statsObj;
            }
            queryDict["query"] = queryInner;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = JsonContent.Create(queryDict);
            AddCommonHeaders(request, sessionId);

            // 记录请求体，便于诊断 400 错误。
            var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            AppLogger.Instance.Info($"搜索请求：POST {url} body={requestBody}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"交易搜索失败：{(int)response.StatusCode} {json}");
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException("POESESSID 无效或已过期，请在设置页重新登录获取。");
                }
                throw new HttpRequestException($"搜索失败：{(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new TradeSearchResult
            {
                SearchId = root.GetProperty("id").GetString() ?? "",
                Total = root.TryGetProperty("total", out var totalElement) ? totalElement.GetInt32() : 0,
            };

            if (root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var idElement in resultElement.EnumerateArray())
                {
                    if (idElement.ValueKind == JsonValueKind.String)
                    {
                        result.ResultIds.Add(idElement.GetString() ?? "");
                    }
                }
            }

            return result;
        }
        finally
        {
            await Task.Delay(RequestDelayMs, cancellationToken);
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// 预加载 stats 数据到缓存。参考 xiletrade-master 启动时同步加载所有静态数据。
    /// 在登录后后台调用，避免首次查价时阻塞。
    /// </summary>
    public async Task PreloadStatsAsync(string? sessionId, CancellationToken ct = default)
    {
        if (_statsList != null && DateTime.Now - _statsCacheTime < StatsCacheExpiry)
        {
            return;
        }
        try
        {
            await GetStatsAsync(sessionId, ct);
            AppLogger.Instance.Info("stats 预加载完成");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"stats 预加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 获取 stats 数据并缓存为扁平列表。保留原始 stat 文本（含 # 占位符），用于正则匹配。
    /// 参考 xiletrade-master 的 FilterData 加载方式。
    /// </summary>
    private async Task<List<StatEntry>> GetStatsAsync(string? sessionId, CancellationToken ct)
    {
        if (_statsList != null && DateTime.Now - _statsCacheTime < StatsCacheExpiry)
        {
            return _statsList;
        }

        var url = $"{BaseUrl}/data/stats";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommonHeaders(request, sessionId);

        await _rateLimiter.WaitAsync(ct);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"获取 stats 数据失败：{(int)response.StatusCode}");
                return [];
            }

            var list = new List<StatEntry>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var category in resultArr.EnumerateArray())
                {
                    if (!category.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in entries.EnumerateArray())
                    {
                        var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var text = entry.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(text))
                        {
                            // 清理 [key|value] 格式标记，保留原始 # 占位符。
                            var cleaned = CleanModDescription(text);
                            var categoryPrefix = id.Contains('.') ? id.Split('.')[0] : "";
                            list.Add(new StatEntry(cleaned, id, categoryPrefix));
                        }
                    }
                }
            }

            _statsList = list;
            _statsCacheTime = DateTime.Now;
            var sample = list.Take(5).Select(e => $"[{e.Text}]={e.Id}");
            AppLogger.Instance.Info($"stats 数据缓存：{list.Count} 条，样本：{string.Join(" | ", sample)}");
            return list;
        }
        finally
        {
            await Task.Delay(RequestDelayMs, ct);
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// 手动导出 stats 缓存到 data/stats_cache_debug.json，供设置页调用。
    /// 若缓存未加载，会先从 API 拉取；已加载且未过期则直接复用。
    /// 返回导出的条目数；若为 0 表示未获取到数据（如 POESESSID 无效或网络异常）。
    /// </summary>
    public async Task<int> DumpStatsCacheAsync(string? sessionId, CancellationToken ct = default)
    {
        var cache = await GetStatsAsync(sessionId, ct);
        if (cache.Count == 0)
        {
            AppLogger.Instance.Warn("stats 缓存为空，跳过导出。请检查 POESESSID 是否有效及网络连接。");
            return 0;
        }
        DumpStatsCacheToFile(cache);
        return cache.Count;
    }

    /// <summary>
    /// 将词缀文本列表匹配到 stat ID 列表，同时解析出词缀的具体数值。
    /// 参考 xiletrade-master 的 ModFilter + ModInfoParse 匹配策略：
    /// 1. 正则精确匹配：构建 ^pattern$ 正则，# 替换为数字匹配模式，同时匹配 stat 文本中的 # 占位符
    /// 2. Levenshtein 模糊匹配：作为兜底，距离阈值 = max(1, len/8)
    /// </summary>
    private async Task<List<(string id, double? value)>> MatchModsToStatIdsAsync(
        List<(string text, string type)> modTexts, string? sessionId, CancellationToken ct)
    {
        var stats = await GetStatsAsync(sessionId, ct);
        var result = new List<(string id, double? value)>();

        foreach (var (modText, modType) in modTexts)
        {
            // 1. 清理 [key|value] 格式标记。
            var cleaned = CleanModDescription(modText);

            // 2. 剥离 tier 范围括号 (min-max)，提取数值。
            var (stripped, _, _) = StripTierRanges(cleaned);
            var modValue = ExtractModValue(stripped);

            // 3. 将数值替换为 # 占位符，构建归一化文本。
            var normalized = DecimalPattern.Replace(stripped, "#").Trim();
            // 统一 +# → #（API stat 文本通常不带 + 号）。
            normalized = normalized.Replace("+#", "#");

            var expectedCategory = MapModTypeToCategory(modType);

            // 阶段1：正则精确匹配。
            var regex = BuildStatRegex(normalized);
            var regexMatches = stats.Where(s => regex.IsMatch(s.Text)).ToList();
            if (regexMatches.Count > 0)
            {
                var candidates = regexMatches.Select(e => (e.Id, e.Category)).ToList();
                var statId = PickStatByCategory(candidates, expectedCategory, modText);
                result.Add((statId, modValue));
                AppLogger.Instance.Info($"词缀正则匹配：'{modText}' → {statId}（{regexMatches.Count} 个命中）");
                continue;
            }

            // 阶段2：Levenshtein 模糊匹配。
            var bestMatch = FindLevenshteinMatch(normalized, stats, expectedCategory);
            if (bestMatch != null)
            {
                result.Add((bestMatch.Id, modValue));
                AppLogger.Instance.Info($"词缀模糊匹配：'{modText}' → '{bestMatch.Text}' (stat: {bestMatch.Id}, 距离={bestMatch.Distance})");
                continue;
            }

            AppLogger.Instance.Warn($"词缀未匹配：{modText} (类型: {modType}, 归一化: {normalized})");
        }

        return result;
    }

    /// <summary>
    /// 构建 stat 匹配正则。参考 xiletrade-master 的 GetInputRegex 实现。
    /// 将归一化文本中的 # 替换为同时匹配数字和 # 占位符的模式，构建 ^...$ 完整匹配正则。
    /// </summary>
    private static Regex BuildStatRegex(string normalizedText)
    {
        // 转义正则特殊字符。
        var escaped = Regex.Escape(normalizedText);
        // 将转义后的 \# 替换为数字匹配模式（同时匹配 # 占位符和实际数字）。
        var pattern = EscapedDiezePattern.Replace(escaped, DecimalPatternDieze);
        // 放宽 +# 模式：将 \+\# 替换为 [+]?\\#（使 + 号可选）。
        pattern = pattern.Replace(@"\+" + DecimalPatternDieze, "[+]?" + DecimalPatternDieze);
        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 剥离词缀文本中的 tier 范围括号 (min-max) 或 (val)。
    /// 参考 xiletrade-master 的 ItemModifier.ParseTierValues 实现。
    /// 返回剥离后的文本和 tier 范围值。
    /// </summary>
    private static (string text, double? tierMin, double? tierMax) StripTierRanges(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, null, null);

        double? tierMin = null;
        double? tierMax = null;

        // 匹配括号内容，提取 min-max 范围。
        var match = TierRangeRegex.Match(text);
        if (match.Success)
        {
            var inner = match.Value.Trim('(', ')', ' ');
            // 尝试解析 min-max 格式。
            var parts = inner.Split('-');
            if (parts.Length == 2)
            {
                if (double.TryParse(parts[0], out var min)) tierMin = min;
                if (double.TryParse(parts[1], out var max)) tierMax = max;
            }
            else if (double.TryParse(inner, out var val))
            {
                tierMin = val;
                tierMax = val;
            }
        }

        // 移除括号范围。
        var stripped = TierRangeRegex.Replace(text, "");
        return (stripped.Trim(), tierMin, tierMax);
    }

    /// <summary>
    /// 使用 Levenshtein 距离进行模糊匹配。参考 xiletrade-master 的 ParseWithLevenshtein 实现。
    /// 距离阈值 = max(1, text.Length / 8)。
    /// </summary>
    private static StatEntry? FindLevenshteinMatch(string normalizedText, List<StatEntry> stats, string expectedCategory)
    {
        if (string.IsNullOrEmpty(normalizedText) || stats.Count == 0) return null;

        var bestDistance = int.MaxValue;
        StatEntry? bestEntry = null;
        var maxDistance = Math.Max(1, normalizedText.Length / 8);

        foreach (var entry in stats)
        {
            // 长度差异过大则跳过。
            if (Math.Abs(entry.Text.Length - normalizedText.Length) > maxDistance) continue;

            var distance = LevenshteinDistance(normalizedText, entry.Text);
            if (distance > maxDistance) continue;

            // 优先匹配期望分类。
            if (distance < bestDistance ||
                (distance == bestDistance && bestEntry != null && 
                 !string.IsNullOrEmpty(expectedCategory) && entry.Category == expectedCategory &&
                 bestEntry.Category != expectedCategory))
            {
                bestDistance = distance;
                bestEntry = entry;
                if (distance == 0) break;
            }
        }

        return bestEntry != null 
            ? bestEntry with { Distance = bestDistance } 
            : null;
    }

    /// <summary>
    /// 计算 Levenshtein 编辑距离。参考 xiletrade-master 使用的 FuzzySharp 库算法。
    /// </summary>
    private static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return source.Length;

        var n = source.Length;
        var m = target.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLower(source[i - 1]) == char.ToLower(target[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// 把游戏内词缀类型映射到 POE2 API 的 stat 分类前缀。
    /// 基底属性/隐式属性 → implicit；前缀/后缀/传奇属性 → explicit；打造属性 → crafted。
    /// </summary>
    private static string MapModTypeToCategory(string modType)
    {
        if (string.IsNullOrEmpty(modType))
        {
            return "";
        }
        return modType switch
        {
            "基底属性" or "隐式属性" or "Implicit" => "implicit",
            "前缀属性" or "后缀属性" or "传奇属性" or "显式属性" or "Explicit" or "Prefix" or "Suffix" => "explicit",
            "打造属性" or "Crafted" => "crafted",
            "附魔" or "Enchant" => "enchant",
            _ => ""
        };
    }

    /// <summary>
    /// 从候选 stat ID 列表中，按期望分类优先挑选，找不到则回退到第一个。
    /// </summary>
    private static string PickStatByCategory(List<(string Id, string Category)> candidates, string expectedCategory, string modText)
    {
        if (!string.IsNullOrEmpty(expectedCategory))
        {
            var matched = candidates.FirstOrDefault(c => c.Category == expectedCategory);
            if (matched.Id != null)
            {
                if (candidates.Count > 1)
                {
                    AppLogger.Instance.Info($"词缀分类匹配：'{modText}' 期望={expectedCategory}, 命中 {matched.Id}（候选共 {candidates.Count} 个）");
                }
                return matched.Id;
            }
            if (candidates.Count > 1)
            {
                var allCats = string.Join(",", candidates.Select(c => c.Category));
                AppLogger.Instance.Warn($"词缀分类未命中：'{modText}' 期望={expectedCategory}, 实际候选分类=[{allCats}], 回退到第一个");
            }
        }
        return candidates[0].Id;
    }

    /// <summary>
    /// 从词缀文本中提取第一个数值（在括号范围之前）。
    /// 例如 "+294 (262-300) 点闪避值" → 294
    ///      "闪避值提高 29 (27-32)%" → 29
    ///      "当你击败稀有或传奇敌人时使用" → null
    /// </summary>
    private static double? ExtractModValue(string text)
    {
        // 匹配括号前的第一个数字（含小数和负号）。
        var match = ModValueRegex.Match(text);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var value))
        {
            return value;
        }
        return null;
    }

    /// <summary>
    /// 将 stats 缓存以易读的 JSON 格式写出到 data/stats_cache_debug.json。
    /// 按分类分组、按文本排序，便于人工查找。
    /// </summary>
    private static void DumpStatsCacheToFile(List<StatEntry> stats)
    {
        try
        {
            var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dataDir);
            var cachePath = System.IO.Path.Combine(dataDir, "stats_cache_debug.json");

            // 按文本分组，每个文本对应所有分类的 stat ID。
            var dumpObj = stats
                .GroupBy(e => e.Text)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(v => v.Category).Select(v => new { id = v.Id, category = v.Category }));

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var dumpJson = JsonSerializer.Serialize(dumpObj, options);
            System.IO.File.WriteAllText(cachePath, dumpJson);
            AppLogger.Instance.Info($"stats 缓存已写出：{cachePath}（{stats.Count} 条）");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"写出 stats 缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 根据搜索 ID 分批获取 listing 详情。
    /// </summary>
    public async Task<List<TradeListing>> FetchAsync(
        string searchId,
        IReadOnlyList<string> ids,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var listings = new List<TradeListing>();
        if (string.IsNullOrWhiteSpace(searchId) || ids.Count == 0)
        {
            return listings;
        }

        for (var i = 0; i < ids.Count; i += MaxFetchIds)
        {
            var batch = ids.Skip(i).Take(MaxFetchIds).ToList();
            var batchListings = await FetchBatchAsync(searchId, batch, sessionId, cancellationToken);
            listings.AddRange(batchListings);

            // 限制查询数量，避免过多请求。
            if (listings.Count >= 10)
            {
                break;
            }
        }

        return listings;
    }

    private async Task<List<TradeListing>> FetchBatchAsync(
        string searchId,
        IReadOnlyList<string> ids,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var idsParam = string.Join(",", ids);
            var url = $"{BaseUrl}/fetch/{idsParam}?query={Uri.EscapeDataString(searchId)}";

            var json = await SendWithRateLimitAsync(url, HttpMethod.Get, null, sessionId, cancellationToken);
            if (string.IsNullOrEmpty(json))
            {
                return [];
            }

            return ParseFetchResponse(json);
        }
        finally
        {
            await Task.Delay(RequestDelayMs, cancellationToken);
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// 发送请求并在收到 429 限流时按 Retry-After 头等待后重试一次。
    /// 参考 xiletrade-master PoeApiService.ApplyCooldown：根据响应头驱动退避。
    /// </summary>
    private async Task<string> SendWithRateLimitAsync(
        string url, HttpMethod method, string? jsonBody, string? sessionId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            AddCommonHeaders(request, sessionId);

            using var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var retryAfter = GetRetryAfterSeconds(response);
                AppLogger.Instance.Warn($"收到 429 限流，等待 {retryAfter} 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"交易请求失败：{(int)response.StatusCode} {json}");
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException("POESESSID 无效或已过期，请在设置页重新登录获取。");
                }
                return "";
            }

            return json;
        }
        return "";
    }

    /// <summary>解析 Retry-After 响应头（秒），无则返回默认退避秒数。</summary>
    private static int GetRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var val = values.FirstOrDefault();
            if (int.TryParse(val, out var sec) && sec > 0)
            {
                return sec;
            }
        }
        return RateLimitBackoffSec;
    }

    private static List<TradeListing> ParseFetchResponse(string json)
    {
        var listings = new List<TradeListing>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var resultElement) ||
                resultElement.ValueKind != JsonValueKind.Array)
            {
                return listings;
            }

            foreach (var item in resultElement.EnumerateArray())
            {
                if (!item.TryGetProperty("listing", out var listingElement))
                {
                    continue;
                }

                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                var accountName = "";
                var stashName = "";
                decimal amount = 0;
                var currency = "";

                if (listingElement.TryGetProperty("account", out var accountElement) &&
                    accountElement.TryGetProperty("name", out var accountNameElement))
                {
                    accountName = accountNameElement.GetString() ?? "";
                }

                if (listingElement.TryGetProperty("stash", out var stashElement) &&
                    stashElement.TryGetProperty("name", out var stashNameElement))
                {
                    stashName = stashNameElement.GetString() ?? "";
                }

                if (listingElement.TryGetProperty("price", out var priceElement))
                {
                    if (priceElement.TryGetProperty("amount", out var amountElement))
                    {
                        amount = amountElement.GetDecimal();
                    }

                    if (priceElement.TryGetProperty("currency", out var currencyElement))
                    {
                        currency = currencyElement.GetString() ?? "";
                    }
                }

                listings.Add(new TradeListing
                {
                    Id = id,
                    Amount = amount,
                    Currency = currency,
                    AccountName = accountName,
                    StashName = stashName,
                    ItemDetail = ParseItemDetail(item),
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"解析交易详情失败：{ex.Message}");
        }

        return listings;
    }

    /// <summary>
    /// 从 fetch 响应的 item 节点解析结构化物品详情。
    /// </summary>
    private static ItemDetailModel ParseItemDetail(JsonElement itemElement)
    {
        var model = new ItemDetailModel();

        if (!itemElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return model;
        }

        // 名称 + 基底类型 + 稀有度。
        model.Name = GetCleanString(item, "name");
        model.TypeLine = GetCleanString(item, "typeLine");
        model.Rarity = GetCleanString(item, "rarity");

        // 物品等级。
        if (item.TryGetProperty("ilvl", out var ilvlEl) && ilvlEl.TryGetInt32(out var ilvl) && ilvl > 0)
        {
            model.ItemLevel = ilvl;
        }

        // 属性。
        if (item.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var prop in propsEl.EnumerateArray())
            {
                var rawName = GetCleanString(prop, "name");
                // 清理 [key|value] 格式标记 → value
                var propName = CleanModDescription(rawName);

                // 获取 displayMode（0=追加, 3=占位符替换）
                var displayMode = 0;
                if (prop.TryGetProperty("displayMode", out var dmEl) && dmEl.TryGetInt32(out var dm))
                {
                    displayMode = dm;
                }

                // 提取 values 数组中的字符串值。
                var valStrs = new List<string>();
                if (prop.TryGetProperty("values", out var valsEl) && valsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in valsEl.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                        {
                            var first = v.EnumerateArray().First();
                            if (first.ValueKind == JsonValueKind.String)
                            {
                                valStrs.Add(first.GetString() ?? "");
                            }
                        }
                        else if (v.ValueKind == JsonValueKind.String)
                        {
                            valStrs.Add(v.GetString() ?? "");
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(propName) && valStrs.Count == 0)
                {
                    continue;
                }

                string propText;
                if (displayMode == 3 && valStrs.Count > 0)
                {
                    // displayMode=3: 用 values 替换 name 中的 {0}, {1}, ... 占位符。
                    propText = propName;
                    for (var i = 0; i < valStrs.Count; i++)
                    {
                        propText = propText.Replace($"{{{i}}}", valStrs[i]);
                    }
                }
                else if (valStrs.Count > 0)
                {
                    // displayMode=0 或其它: name: value1, value2
                    propText = $"{propName}: {string.Join(", ", valStrs)}";
                }
                else
                {
                    // 无值，只有名称。
                    propText = propName;
                }

                model.Properties.Add(propText);
            }
        }

        // 需求。
        if (item.TryGetProperty("requirements", out var reqsEl) && reqsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in reqsEl.EnumerateArray())
            {
                var reqName = CleanModDescription(GetCleanString(req, "name"));
                var reqValues = "";
                if (req.TryGetProperty("values", out var valsEl) && valsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in valsEl.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                        {
                            var first = v.EnumerateArray().First();
                            if (first.ValueKind == JsonValueKind.String)
                            {
                                reqValues = first.GetString() ?? "";
                            }
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(reqValues))
                {
                    model.Requirements.Add($"{reqName} {reqValues}");
                }
            }
        }

        // 插槽。
        if (item.TryGetProperty("sockets", out var socketsEl) && socketsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var _ in socketsEl.EnumerateArray())
            {
                model.Sockets++;
            }
        }

        // 隐式属性。
        model.ImplicitMods = GetStringArray(item, "implicitMods");
        // 显式属性。
        model.ExplicitMods = GetStringArray(item, "explicitMods");
        // 打造属性。
        model.CraftedMods = GetStringArray(item, "craftedMods");

        // 腐化标记。
        if (item.TryGetProperty("corrupted", out var corrEl) && corrEl.ValueKind == JsonValueKind.True)
        {
            model.Corrupted = true;
        }

        // 价格。
        if (itemElement.TryGetProperty("listing", out var listingEl) &&
            listingEl.TryGetProperty("price", out var priceEl))
        {
            var amount = priceEl.TryGetProperty("amount", out var amtEl) ? amtEl.GetDecimal() : 0;
            var currency = priceEl.TryGetProperty("currency", out var curEl) ? curEl.GetString() ?? "" : "";
            if (amount > 0)
            {
                model.PriceText = $"~b/o {amount} {currency}";
            }
        }

        return model;
    }

    /// <summary>
    /// 从 JsonElement 获取字符串数组属性。支持纯字符串数组和含 description 字段的对象数组。
    /// </summary>
    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        var result = new List<string>();
        if (element.TryGetProperty(propertyName, out var arrEl) && arrEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arrEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(CleanModDescription(item.GetString() ?? ""));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    // POE2 API 格式：{ "description": "...", "hash": "...", "mods": [...] }
                    if (item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                    {
                        result.Add(CleanModDescription(descEl.GetString() ?? ""));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 清理词缀描述中的格式标记：[key|value] → value。
    /// 例如 "[SpiritOfTheCatPossessedPlayer|豹之灵][PlayerPossessed|附身] 11 秒" → "豹之灵附身 11 秒"
    /// </summary>
    private static readonly Regex ModFormatRegex = new(@"\[.+?\|(.+?)\]", RegexOptions.Compiled);
    private static string CleanModDescription(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // [key|value] → value
        var cleaned = ModFormatRegex.Replace(text, "$1");
        return cleaned.Trim();
    }

    /// <summary>
    /// 从 JsonElement 获取清理后的字符串（去掉 POE API 的格式标记）。
    /// </summary>
    private static string GetCleanString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var text = prop.GetString() ?? "";
        // 去掉 <<set:XX>> 格式标记。
        var idx = text.LastIndexOf(">>");
        if (idx >= 0 && text.StartsWith("<<"))
        {
            text = text[(idx + 2)..];
        }
        return text.Trim();
    }

    private void AddCommonHeaders(HttpRequestMessage request, string? sessionId)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Poe2PriceGui/1.0");
        // POST 请求需要 Accept 头（参考 xiletrade-master NetService.SendHTTP）。
        if (request.Method == HttpMethod.Post)
        {
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }
        // 国服交易接口需要 Origin/Referer 头通过 CSRF 校验；国际服使用 pathofexile.com。
        if (IsChina)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://poe.game.qq.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://poe.game.qq.com/trade2/search");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://www.pathofexile.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.pathofexile.com/trade2/search");
        }
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"POESESSID={sessionId}");
        }
    }
}

/// <summary>
/// 搜索结果摘要。
/// </summary>
public class TradeSearchResult
{
    public string SearchId { get; set; } = "";
    public List<string> ResultIds { get; set; } = [];
    public int Total { get; set; }
}

/// <summary>
/// 一条市集 listing。
/// </summary>
public class TradeListing
{
    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["divine"] = "神圣石",
        ["exalted"] = "崇高石",
        ["chaos"] = "混沌石",
        ["mirror"] = "镜子",
        ["vaal"] = "瓦尔宝珠",
        ["regal"] = "富豪宝珠",
        ["jeweller"] = "珠宝匠宝珠",
        ["fusing"] = "链接石",
        ["chromatic"] = "色彩石",
        ["alchemy"] = "点金石",
        ["chance"] = "机会石",
        ["alteration"] = "改造石",
        ["scouring"] = "重铸石",
        ["blessed"] = "祝福石",
        ["cartographer"] = "制图师",
        ["gemcutter"] = "宝石匠",
        ["annulment"] = "抹灭石",
        ["binding"] = "绑定石",
        ["engineer"] = "工程师石",
        ["harbinger"] = "先驱石",
        ["ancient"] = "远古石",
        ["silver"] = "银币",
        ["gold"] = "金币",
        ["bauble"] = "玻璃弹珠",
        ["blacksmith"] = "磨刀石",
        ["transmutation"] = "蜕变石",
        ["armourer"] = "护甲片",
        ["augmentation"] = "增幅石",
        ["deck"] = "命运卡",
    };

    public string Id { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string StashName { get; set; } = "";
    public ItemDetailModel ItemDetail { get; set; } = new();

    public string DisplayPrice
    {
        get
        {
            var name = Currency;
            foreach (var kv in CurrencyNames)
            {
                if (Currency.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    name = kv.Value;
                    break;
                }
            }
            var amt = Amount % 1 == 0 ? Amount.ToString("0") : Amount.ToString("0.##");
            return $"{amt}{name}";
        }
    }
}
