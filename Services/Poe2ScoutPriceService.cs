using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 从 poe2scout.com 获取并归一化国际服通货价格数据。
/// 参考build_poe2scout_price_patch.py 中的 fetch_scout_data / collect_price_observations /
/// choose_best_prices 逻辑。
/// </summary>
public class Poe2ScoutPriceService : IPriceService
{
    private const string DefaultApiBase = "https://api.poe2scout.com";
    private const string DefaultLeague = "runes";

    private readonly HttpClient _httpClient;
    private readonly string _apiBase;
    private readonly string _league;

    public string DataSourceLabel => "poe2scout.com (国际服)";
    public bool IsChina => false;

    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="league">poe2scout 联赛短名，例如 "runes"。</param>
    /// <param name="apiBase">API 根地址，默认 https://api.poe2scout.com。</param>
    public Poe2ScoutPriceService(HttpClient httpClient, string league = DefaultLeague, string? apiBase = null)
    {
        _httpClient = httpClient;
        _league = string.IsNullOrWhiteSpace(league) ? DefaultLeague : league;
        _apiBase = string.IsNullOrWhiteSpace(apiBase) ? DefaultApiBase : apiBase.TrimEnd('/');
    }

    public async Task<ObservableCollection<PoecurrencyItem>> FetchPricesAsync(CancellationToken cancellationToken = default)
    {
        // 并行拉取 ReferenceCurrencies / SnapshotPairs。
        var snapshotPairsTask = GetJsonAsync($"{_apiBase}/poe2/Leagues/{_league}/SnapshotPairs", cancellationToken);
        var referenceCurrenciesTask = GetJsonAsync($"{_apiBase}/poe2/Leagues/{_league}/ReferenceCurrencies", cancellationToken);

        await Task.WhenAll(snapshotPairsTask, referenceCurrenciesTask);

        var snapshotPairs = snapshotPairsTask.Result;
        var referenceCurrencies = referenceCurrenciesTask.Result;

        // 收集所有价格观测，按 api_id 分组，取交易量最大者。
        var observations = CollectPriceObservations(snapshotPairs);
        var best = ChooseBestPrices(observations, referenceCurrencies);

        return BuildItems(best);
    }

    /// <summary>
    /// 解析 SnapshotPairs，按 api_id 收集价格观测。
    /// 每个 pair 有 CurrencyOne / CurrencyTwo 两侧，分别取 RelativePrice / ValueTraded。
    /// </summary>
    private static Dictionary<string, List<ScoutObservation>> CollectPriceObservations(JsonElement snapshotPairs)
    {
        var byApiId = new Dictionary<string, List<ScoutObservation>>(StringComparer.OrdinalIgnoreCase);

        if (snapshotPairs.ValueKind != JsonValueKind.Array)
        {
            return byApiId;
        }

        foreach (var pair in snapshotPairs.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var c1 = pair.GetProperty("CurrencyOne");
            var c2 = pair.GetProperty("CurrencyTwo");
            var pairName = $"{GetText(c1)} / {GetText(c2)}";

            foreach (var (currencyKey, dataKey) in new[] { ("CurrencyOne", "CurrencyOneData"), ("CurrencyTwo", "CurrencyTwoData") })
            {
                if (!pair.TryGetProperty(currencyKey, out var currency) || currency.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (!pair.TryGetProperty(dataKey, out var sideData) || sideData.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var price = GetDecimal(sideData, "RelativePrice");
                if (price <= 0)
                {
                    continue;
                }

                var valueTraded = GetDecimal(sideData, "ValueTraded");
                var apiId = GetString(currency, "ApiId");
                if (string.IsNullOrEmpty(apiId))
                {
                    continue;
                }

                var text = GetText(currency);
                var category = GetString(currency, "CategoryApiId");

                byApiId.TryGetValue(apiId, out var list);
                if (list == null)
                {
                    list = new List<ScoutObservation>();
                    byApiId[apiId] = list;
                }
                list.Add(new ScoutObservation
                {
                    ApiId = apiId,
                    EnName = text,
                    Category = category,
                    IconUrl = GetString(currency, "IconUrl"),
                    PriceExalted = price,
                    ValueTraded = valueTraded,
                    SourcePair = pairName,
                });
            }
        }

        return byApiId;
    }

    /// <summary>
    /// 每个 api_id 取交易量最大（其次价格最高）的观测。
    /// 之后用 ReferenceCurrencies 补全：RelativePrice=1 表示基础货币（崇高石），价格固定为 1。
    /// </summary>
    private static Dictionary<string, ScoutObservation> ChooseBestPrices(
        Dictionary<string, List<ScoutObservation>> observations,
        JsonElement referenceCurrencies)
    {
        var best = new Dictionary<string, ScoutObservation>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in observations)
        {
            if (kv.Value == null || kv.Value.Count == 0)
            {
                continue;
            }
            best[kv.Key] = kv.Value
                .OrderByDescending(o => o.ValueTraded)
                .ThenByDescending(o => o.PriceExalted)
                .First();
        }

        if (referenceCurrencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var refItem in referenceCurrencies.EnumerateArray())
            {
                if (refItem.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var apiId = GetString(refItem, "ApiId");
                if (string.IsNullOrEmpty(apiId))
                {
                    continue;
                }

                var text = GetText(refItem);
                var relativePrice = GetDecimal(refItem, "RelativePrice");

                if (relativePrice == 1m)
                {
                    // 基础货币（崇高石），价格固定为 1。
                    best[apiId] = new ScoutObservation
                    {
                        ApiId = apiId,
                        EnName = text,
                        Category = "currency",
                        IconUrl = GetString(refItem, "IconUrl"),
                        PriceExalted = 1m,
                        ValueTraded = 0m,
                        SourcePair = "ReferenceCurrencies",
                    };
                }
                else if (!best.ContainsKey(apiId) && relativePrice > 0)
                {
                    best[apiId] = new ScoutObservation
                    {
                        ApiId = apiId,
                        EnName = text,
                        Category = "currency",
                        IconUrl = GetString(refItem, "IconUrl"),
                        PriceExalted = relativePrice,
                        ValueTraded = 0m,
                        SourcePair = "ReferenceCurrencies",
                    };
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 将 best 字典转为 PoecurrencyItem 集合，推导神圣石比例。
    /// </summary>
    private static ObservableCollection<PoecurrencyItem> BuildItems(Dictionary<string, ScoutObservation> best)
    {
        var result = new ObservableCollection<PoecurrencyItem>();

        // 推导神圣石/崇高石比例：divine 的 price_exalted 即为 D/E 比例。
        decimal divineExaltedRatio = 0;
        foreach (var kv in best)
        {
            if (IsDivine(kv.Value.EnName) || string.Equals(kv.Key, "divine", StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Value.PriceExalted > divineExaltedRatio)
                {
                    divineExaltedRatio = kv.Value.PriceExalted;
                }
            }
        }

        foreach (var kv in best)
        {
            var obs = kv.Value;
            if (obs.PriceExalted <= 0)
            {
                continue;
            }

            // 跳过基础货币（崇高石本身），显示价格为空。
            if (string.Equals(kv.Key, "exalted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryLabel = MapCategoryLabel(obs.Category);
            var unitNote = "e";

            result.Add(new PoecurrencyItem
            {
                CategoryLabel = categoryLabel,
                ItemName = obs.EnName,
                IconUrl = string.IsNullOrEmpty(obs.IconUrl) ? null : obs.IconUrl,
                LatestBuy1 = obs.PriceExalted,
                LatestSell1 = obs.PriceExalted,
                BuyAverage = obs.PriceExalted,
                SellAverage = obs.PriceExalted,
                PreviousBuy1 = 0,
                CurrencyUnit = "e",
                HasError = false,
                ErrorInfo = "",
                PriceExalted = obs.PriceExalted,
                DivineExaltedRatio = divineExaltedRatio,
                SourcePair = $"poe2scout.com/{obs.Category}/{obs.SourcePair}/{unitNote}",
            });
        }

        return result;
    }

    private static string MapCategoryLabel(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return "通货";
        }
        return category switch
        {
            "currency" => "通货",
            "fragment" => "碎片",
            "rune" => "符文",
            _ => category,
        };
    }

    private static bool IsDivine(string name)
    {
        var normalized = (name ?? "").ToLowerInvariant()
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace(" ", "")
            .Replace("\u3000", "");
        return normalized is "divineorb" or "divine" or "神圣石" or "神圣宝珠";
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    #region Json Helpers

    private static string GetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                var text = value.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }
        return "";
    }

    private static string GetText(JsonElement element)
    {
        return GetString(element, "Text", "Name", "name");
    }

    private static decimal GetDecimal(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
                {
                    return decimalValue;
                }
                if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }
        return 0;
    }

    #endregion

    private sealed class ScoutObservation
    {
        public string ApiId { get; set; } = "";
        public string EnName { get; set; } = "";
        public string Category { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public decimal PriceExalted { get; set; }
        public decimal ValueTraded { get; set; }
        public string SourcePair { get; set; } = "";
    }
}
