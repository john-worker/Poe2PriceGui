using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 国服 POE2 市集交易接口服务。
/// </summary>
public class PoeTradeService
{
    private const string BaseUrl = "https://poe.game.qq.com/api/trade2";
    private const int MaxFetchIds = 10;
    private const int RequestDelayMs = 1200;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);

    // 缓存的 stats 数据：key = 归一化文本，value = 该文本在所有分类下的 stat ID 列表（含分类前缀）。
    // 同一文本可能出现在多个分类中（如「所有元素抗性 +#」在 implicit 与 explicit 下都有），需保留全部以正确匹配。
    private Dictionary<string, List<(string id, string category)>>? _statsCache;
    private DateTime _statsCacheTime;
    private static readonly TimeSpan StatsCacheExpiry = TimeSpan.FromHours(6);

    // 用于归一化词缀文本的正则：匹配 "(...)" 范围和独立数字。
    private static readonly Regex RangeRegex = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\d+\.?\d*", RegexOptions.Compiled);
    // 匹配括号范围之前的第一个数值（含小数和负号），用于精确搜索。
    private static readonly Regex ModValueRegex = new(@"(-?\d+\.?\d*)\s*\(", RegexOptions.Compiled);
    // 匹配连续的中文字符，用于关键词回退搜索。
    private static readonly Regex ChineseCharRegex = new(@"[\u4e00-\u9fff]+", RegexOptions.Compiled);

    public PoeTradeService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 按物品名称/基底搜索，返回搜索结果摘要。
    /// searchByType=true 时按基底(type)查询，否则按名称(name)查询（适用于传奇物品）。
    /// itemLevel 不为 null 时添加物品等级筛选。
    /// rarity 不为空时添加稀有度筛选。
    /// selectedMods 不为空时按词缀过滤：每个元素 = (词缀文本, 词缀类型)，词缀类型用于映射到正确的 stat 分类（implicit/explicit/crafted）。
    /// isExactSearch=true 时按词缀的具体数值过滤。
    /// </summary>
    public async Task<TradeSearchResult> SearchAsync(
        string league,
        string itemName,
        string? sessionId,
        bool searchByType = true,
        int? itemLevel = null,
        string? rarity = null,
        List<(string text, string type)>? selectedMods = null,
        bool isExactSearch = false,
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

            // 构建 misc_filters.filters：物品等级筛选（只设下限）。
            var miscFiltersInner = new Dictionary<string, object>();
            if (itemLevel.HasValue)
            {
                miscFiltersInner["ilvl"] = new { min = itemLevel.Value };
            }

            // 构建 type_filters.filters：稀有度筛选。
            var typeFiltersInner = new Dictionary<string, object>();
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
                }
            }

            // POE2 API 要求 type_filters/misc_filters 内层嵌套 filters 对象。
            var filtersDict = new Dictionary<string, object>();
            if (miscFiltersInner.Count > 0)
            {
                filtersDict["misc_filters"] = new { filters = miscFiltersInner };
            }
            if (typeFiltersInner.Count > 0)
            {
                filtersDict["type_filters"] = new { filters = typeFiltersInner };
            }

            object filtersObj = filtersDict.Count > 0 ? filtersDict : new { };

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

            // 使用 Dictionary 灵活构建 query，因为 stats 可能不存在。
            var queryDict = new Dictionary<string, object?>
            {
                ["sort"] = new { price = "asc" },
            };

            var queryInner = new Dictionary<string, object?>();
            if (searchByType)
            {
                queryInner["type"] = itemName;
            }
            else
            {
                queryInner["name"] = itemName;
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
    /// 获取 stats 数据并缓存。返回 key=归一化文本, value=该文本对应的所有 (stat ID, 分类前缀) 列表。
    /// 同一文本可能在多个分类下出现（如 implicit 与 explicit 都有「所有元素抗性 +#」），需全部保留。
    /// 分类前缀直接从 stat ID 中提取（如 "explicit.stat_xxx" → "explicit"）。
    /// </summary>
    private async Task<Dictionary<string, List<(string id, string category)>>> GetStatsAsync(string? sessionId, CancellationToken ct)
    {
        if (_statsCache != null && DateTime.Now - _statsCacheTime < StatsCacheExpiry)
        {
            return _statsCache;
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

            var cache = new Dictionary<string, List<(string id, string category)>>();
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
                            // 先清理 [key|value] 格式标记，再归一化。
                            var cleaned = CleanModDescription(text);
                            var normalized = NormalizeModText(cleaned);
                            // 从 stat ID 提取分类前缀（如 "explicit.stat_xxx" → "explicit"）。
                            var categoryPrefix = id.Contains('.') ? id.Split('.')[0] : "";
                            if (!cache.TryGetValue(normalized, out var list))
                            {
                                list = new List<(string, string)>();
                                cache[normalized] = list;
                            }
                            // 避免同一分类下的重复条目。
                            if (!list.Any(x => x.id == id))
                            {
                                list.Add((id, categoryPrefix));
                            }
                        }
                    }
                }
            }

            _statsCache = cache;
            _statsCacheTime = DateTime.Now;
            // 记录前 5 条样本，便于诊断格式问题。
            var sample = cache.Take(5).Select(kv => $"[{kv.Key}]={string.Join(",", kv.Value.Select(v => v.id))}");
            AppLogger.Instance.Info($"stats 数据缓存：{cache.Count} 条，样本：{string.Join(" | ", sample)}");
            return cache;
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
    /// 每个 modTexts 元素 = (词缀文本, 词缀类型)，词缀类型用于映射到正确的 stat 分类（implicit/explicit/crafted）。
    /// </summary>
    private async Task<List<(string id, double? value)>> MatchModsToStatIdsAsync(List<(string text, string type)> modTexts, string? sessionId, CancellationToken ct)
    {
        var stats = await GetStatsAsync(sessionId, ct);
        var result = new List<(string id, double? value)>();

        foreach (var (modText, modType) in modTexts)
        {
            // 先解析数值（在归一化之前）。
            var modValue = ExtractModValue(modText);

            // 清理 [key|value] 格式标记后归一化。
            var cleaned = CleanModDescription(modText);
            var normalized = NormalizeModText(cleaned);

            // 把游戏内词缀类型映射到 API 分类前缀。
            var expectedCategory = MapModTypeToCategory(modType);

            if (stats.TryGetValue(normalized, out var candidates) && candidates.Count > 0)
            {
                var statId = PickStatByCategory(candidates, expectedCategory, modText);
                result.Add((statId, modValue));
                continue;
            }

            // 尝试部分匹配（按归一化文本包含关系）。
            var partial = stats.FirstOrDefault(kv => normalized.Contains(kv.Key) || kv.Key.Contains(normalized));
            if (partial.Value != null && partial.Value.Count > 0)
            {
                var statId = PickStatByCategory(partial.Value, expectedCategory, modText);
                result.Add((statId, modValue));
                AppLogger.Instance.Info($"词缀部分匹配：'{modText}' → '{partial.Key}' (stat: {statId})");
                continue;
            }

            // 关键词回退：提取中文字符作为关键词搜索。
            var keyword = ChineseCharRegex.Match(normalized).Value;
            if (!string.IsNullOrEmpty(keyword))
            {
                var keywordCandidates = stats
                    .Where(kv => kv.Key.Contains(keyword))
                    .SelectMany(kv => kv.Value)
                    .ToList();
                if (keywordCandidates.Count > 0)
                {
                    // 记录所有候选，便于诊断。
                    var candidateStrs = keywordCandidates.Select(c => $"[{c.category}]{c.id}");
                    AppLogger.Instance.Info($"词缀关键词匹配 '{keyword}'：{keywordCandidates.Count} 个候选：{string.Join(" | ", candidateStrs)}");

                    var statId = PickStatByCategory(keywordCandidates, expectedCategory, modText);
                    result.Add((statId, modValue));
                    continue;
                }
            }

            AppLogger.Instance.Warn($"词缀未匹配到 stat ID：{modText} (类型: {modType}, 期望分类: {expectedCategory}, 归一化: {normalized}, 关键词: {keyword})");
        }

        return result;
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
    private static string PickStatByCategory(List<(string id, string category)> candidates, string expectedCategory, string modText)
    {
        if (!string.IsNullOrEmpty(expectedCategory))
        {
            var matched = candidates.FirstOrDefault(c => c.category == expectedCategory);
            if (matched.id != null)
            {
                if (candidates.Count > 1)
                {
                    AppLogger.Instance.Info($"词缀分类匹配：'{modText}' 期望={expectedCategory}, 命中 {matched.id}（候选共 {candidates.Count} 个）");
                }
                return matched.id;
            }
            // 期望分类未命中，记录以便诊断。
            if (candidates.Count > 1)
            {
                var allCats = string.Join(",", candidates.Select(c => c.category));
                AppLogger.Instance.Warn($"词缀分类未命中：'{modText}' 期望={expectedCategory}, 实际候选分类=[{allCats}], 回退到第一个");
            }
        }
        return candidates[0].id;
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
    /// 归一化词缀文本：去掉 "(...)" 范围，将数字替换为 #。
    /// 例如 "+294 (262-300) 点闪避值" → "+# 点闪避值"
    /// </summary>
    private static string NormalizeModText(string text)
    {
        // 去掉 (xxx) 范围标注。
        var noRange = RangeRegex.Replace(text, "");
        // 将数字替换为 #。
        var normalized = NumberRegex.Replace(noRange, "#");
        // 统一去掉 +# 中的 + 号：API stat 文本通常不带 + 号（# 已表示正数），
        // 但游戏内显示的词缀会带 + 号，不处理会导致精确匹配失败。
        normalized = normalized.Replace("+#", "#");
        return normalized.Trim();
    }

    /// <summary>
    /// 将 stats 缓存以易读的 JSON 格式写出到 data/stats_cache_debug.json，
    /// 方便人工查看每个归一化文本对应的全部 stat ID 与分类前缀。
    /// 写出失败不影响主流程。
    /// </summary>
    private static void DumpStatsCacheToFile(Dictionary<string, List<(string id, string category)>> cache)
    {
        try
        {
            var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dataDir);
            var cachePath = System.IO.Path.Combine(dataDir, "stats_cache_debug.json");

            // 按分类前缀分组、再按文本排序，便于人工查找。
            var dumpObj = cache
                .OrderBy(kv => kv.Key)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.OrderBy(v => v.category).Select(v => new { id = v.id, category = v.category }));

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var dumpJson = JsonSerializer.Serialize(dumpObj, options);
            System.IO.File.WriteAllText(cachePath, dumpJson);
            AppLogger.Instance.Info($"stats 缓存已写出：{cachePath}（{cache.Count} 条）");
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

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, sessionId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"交易取详情失败：{(int)response.StatusCode} {json}");
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

    private static void AddCommonHeaders(HttpRequestMessage request, string? sessionId)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Poe2PriceGui/1.0");
        // 国服交易接口需要 Origin/Referer 头通过 CSRF 校验。
        request.Headers.TryAddWithoutValidation("Origin", "https://poe.game.qq.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://poe.game.qq.com/trade2/search");
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
