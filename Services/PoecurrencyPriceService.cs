using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 从 poecurrency.top 获取并归一化价格数据。
/// 参考 build_poe2scout_price_patch.py 中的 normalize_poecurrency_summary 与价格选择逻辑。
/// </summary>
public class PoecurrencyPriceService : IPriceService
{
    private const string DefaultSummaryUrl = "https://poecurrency.top/api/summary?version=2";
    private const string ValidateSummaryUrlFormat = "https://poecurrency.top/api/summary_validate?token={0}&version=2";
    private readonly HttpClient _httpClient;

    public string DataSourceLabel => "poecurrency.top (国服)";
    public bool IsChina => true;

    /// <summary>通货价格查询 Token，为空时使用公共 summary 接口，非空时使用 summary_validate 接口。</summary>
    public string? Token { get; set; }

    public PoecurrencyPriceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// IPriceService 接口实现：使用 Token 属性拉取价格。
    /// </summary>
    Task<ObservableCollection<PoecurrencyItem>> IPriceService.FetchPricesAsync(CancellationToken cancellationToken)
    {
        return FetchPricesAsync(token: Token, cancellationToken: cancellationToken);
    }

    public async Task<ObservableCollection<PoecurrencyItem>> FetchPricesAsync(
        string? summaryUrl = null,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        string url;
        if (!string.IsNullOrWhiteSpace(token))
        {
            url = string.Format(ValidateSummaryUrlFormat, Uri.EscapeDataString(token.Trim()));
        }
        else
        {
            url = string.IsNullOrWhiteSpace(summaryUrl) ? DefaultSummaryUrl : summaryUrl;
        }
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var summary = NormalizeSummary(root);
        return BuildItems(summary);
    }

    /// <summary>
    /// 将接口响应归一化为统一的分类/物品结构。
    /// </summary>
    private static List<PoecurrencyCategory> NormalizeSummary(JsonElement root)
    {
        var categories = new List<PoecurrencyCategory>();

        JsonElement categoryList;
        if (root.ValueKind == JsonValueKind.Array)
        {
            categoryList = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            categoryList = FirstPresent(root, "value", "data", "items", "list", "result");
        }
        else
        {
            throw new InvalidOperationException("poecurrency.top 返回的数据格式不正确。");
        }

        if (categoryList.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("poecurrency.top 返回的分类列表格式不正确。");
        }

        foreach (var categoryElement in categoryList.EnumerateArray())
        {
            if (categoryElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = GetString(categoryElement, "category_label", "category", "label", "name");
            var itemsElement = FirstPresent(categoryElement, "items", "data", "list", "children");
            if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = new List<PoecurrencyRawItem>();
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                if (itemElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(itemElement, "item_name", "name", "itemName", "item");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                items.Add(new PoecurrencyRawItem
                {
                    Name = name,
                    LatestBuy1 = GetDecimal(itemElement, "latest_buy1", "latest_buy", "buy1", "buy_price"),
                    LatestSell1 = GetDecimal(itemElement, "latest_sell1", "latest_sell", "sell1", "sell_price"),
                    BuyAverage = GetDecimal(itemElement, "buy_avg", "avg_buy", "buyAverage", "buy"),
                    SellAverage = GetDecimal(itemElement, "sell_avg", "avg_sell", "sellAverage", "sell"),
                    BuyAverageYesterday = GetDecimal(itemElement, "buy_avg_yesterday", "avg_buy_yesterday"),
                    SellAverageYesterday = GetDecimal(itemElement, "sell_avg_yesterday", "avg_sell_yesterday"),
                    PreviousBuy1 = GetDecimal(itemElement, "prev_buy1", "previous_buy1", "prev_buy"),
                    CurrencyUnit = GetString(itemElement, "currency_unit", "unit"),
                    ExplicitExalted = GetDecimal(itemElement, "e", "price_e", "exalted", "exalted_price"),
                    HasError = GetBool(itemElement, "error"),
                    ErrorInfo = GetString(itemElement, "error_info"),
                });
            }

            categories.Add(new PoecurrencyCategory
            {
                Label = label,
                Items = items,
            });
        }

        return categories;
    }

    private static ObservableCollection<PoecurrencyItem> BuildItems(List<PoecurrencyCategory> categories)
    {
        var result = new ObservableCollection<PoecurrencyItem>();

        // 先推导神圣石/崇高石换算比例。
        // 国服 poecurrency.top 中 Divine Orb 以 E 标价时，其价格即为 D/E 比例。
        decimal divineExaltedRatio = 0;
        foreach (var category in categories)
        {
            foreach (var raw in category.Items)
            {
                if (IsDivine(raw.Name) && GetUnit(raw) == "e")
                {
                    var price = ChooseSimplePrice(raw);
                    if (price > divineExaltedRatio)
                    {
                        divineExaltedRatio = price;
                    }
                }
            }
        }

        foreach (var category in categories)
        {
            foreach (var raw in category.Items)
            {
                var unit = GetUnit(raw);
                var (price, source) = ComputePrice(raw, unit, divineExaltedRatio);
                if (price <= 0)
                {
                    continue;
                }

                decimal priceExalted;
                string unitNote;
                if (unit == "d")
                {
                    priceExalted = divineExaltedRatio > 0 ? price * divineExaltedRatio : 0;
                    unitNote = $"d_to_e@{divineExaltedRatio}";
                }
                else
                {
                    priceExalted = price;
                    unitNote = "e";
                }

                if (IsDivine(raw.Name) && unit == "e" && priceExalted <= 0)
                {
                    priceExalted = price;
                    unitNote = "e";
                }

                if (priceExalted <= 0)
                {
                    continue;
                }

                result.Add(new PoecurrencyItem
                {
                    CategoryLabel = category.Label,
                    ItemName = raw.Name,
                    LatestBuy1 = raw.LatestBuy1,
                    LatestSell1 = raw.LatestSell1,
                    BuyAverage = raw.BuyAverage,
                    SellAverage = raw.SellAverage,
                    PreviousBuy1 = raw.PreviousBuy1,
                    CurrencyUnit = unit,
                    HasError = raw.HasError,
                    ErrorInfo = raw.ErrorInfo,
                    PriceExalted = priceExalted,
                    DivineExaltedRatio = divineExaltedRatio,
                    SourcePair = $"poecurrency.top/{category.Label}/{source}/{unitNote}",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 简化版价格计算：优先复刻 Python 脚本的主要分支。
    /// </summary>
    private static (decimal price, string source) ComputePrice(
        PoecurrencyRawItem raw,
        string unit,
        decimal divineExaltedRatio)
    {
        // 1. 显式的 exalted 价格字段优先级最高。
        if (raw.ExplicitExalted > 0)
        {
            return (raw.ExplicitExalted, "explicit_exalted");
        }

        // 2. 神圣石单独处理。
        if (IsDivine(raw.Name))
        {
            return ComputeDivinePrice(raw);
        }

        // 3. 普通物品：均价作为参考，结合 latest_buy1 / latest_sell1 选择。
        var avg = GeometricMean(raw.BuyAverage, raw.SellAverage);

        if (unit == "d")
        {
            var shifted = TryDigitShiftedDivinePrice(raw);
            if (shifted.price > 0)
            {
                return shifted;
            }

            var latest = ChoosePairPrice(raw.LatestBuy1, raw.LatestSell1, "latest_buy1", "latest_sell1");
            if (latest.price > 0 && !latest.source.EndsWith("spread_gt_5x"))
            {
                return latest;
            }
        }

        if (raw.HasError)
        {
            if (avg > 0)
            {
                return (avg, $"{GeometricMeanSource(raw.BuyAverage, raw.SellAverage)}_error_fallback");
            }

            // 今日均价为 0 时，回退到昨日均价（比 prev_buy1 更稳定，prev_buy1 可能就是触发异常的错误数据）。
            var avgYesterday = GeometricMean(raw.BuyAverageYesterday, raw.SellAverageYesterday);
            if (avgYesterday > 0)
            {
                return (avgYesterday, $"{GeometricMeanSource(raw.BuyAverageYesterday, raw.SellAverageYesterday)}_yesterday_error_fallback");
            }

            if (raw.PreviousBuy1 > 0)
            {
                return (raw.PreviousBuy1, "prev_buy1_error_fallback");
            }
        }

        var latestWithRef = ChoosePairPriceWithReference(
            raw.LatestBuy1,
            raw.LatestSell1,
            "latest_buy1",
            "latest_sell1",
            avg,
            GeometricMeanSource(raw.BuyAverage, raw.SellAverage));
        if (latestWithRef.price > 0)
        {
            return latestWithRef;
        }

        if (avg > 0)
        {
            return (avg, GeometricMeanSource(raw.BuyAverage, raw.SellAverage));
        }

        return (0, "");
    }

    private static (decimal price, string source) ComputeDivinePrice(PoecurrencyRawItem raw)
    {
        var stable = raw.BuyAverage > 0 ? raw.BuyAverage : raw.SellAverage;
        var stableField = raw.BuyAverage > 0 ? "buy_avg" : "sell_avg";

        if (raw.HasError)
        {
            if (stable > 0)
            {
                return (stable, $"{stableField}_divine_error_fallback");
            }

            // 今日均价为 0 时，回退到昨日均价（比 prev_buy1 更稳定）。
            var stableYesterday = raw.BuyAverageYesterday > 0 ? raw.BuyAverageYesterday : raw.SellAverageYesterday;
            var stableYesterdayField = raw.BuyAverageYesterday > 0 ? "buy_avg_yesterday" : "sell_avg_yesterday";
            if (stableYesterday > 0)
            {
                return (stableYesterday, $"{stableYesterdayField}_divine_error_fallback");
            }

            if (raw.PreviousBuy1 > 0)
            {
                return (raw.PreviousBuy1, "prev_buy1_divine_error_fallback");
            }
        }

        // 买卖价差异常（>5x）：选择最接近均价的那个，避免极端挂单污染神圣石比例。
        if (raw.LatestBuy1 > 0 && raw.LatestSell1 > 0
            && SpreadRatio(raw.LatestBuy1, raw.LatestSell1) > 5)
        {
            var closest = ClosestToReference(stable,
                (raw.LatestBuy1, "latest_buy1"),
                (raw.LatestSell1, "latest_sell1"));
            if (closest.price > 0)
            {
                return (closest.price, $"{closest.source}_divine_spread_fallback");
            }
        }

        // 最新买价相对均价异常（>5x）：回退到均价，防止异常挂单影响 D/E 换算。
        if (raw.LatestBuy1 > 0 && stable > 0
            && SpreadRatio(raw.LatestBuy1, stable) > 5)
        {
            return (stable, $"{stableField}_divine_latest_outlier_fallback");
        }

        if (raw.LatestBuy1 > 0)
        {
            return (raw.LatestBuy1, "latest_buy1_divine_ratio");
        }

        if (raw.LatestSell1 > 0)
        {
            return (raw.LatestSell1, "latest_sell1_divine_ratio");
        }

        if (stable > 0)
        {
            return (stable, $"{stableField}_divine_ratio");
        }

        return (0, "");
    }

    private static decimal ChooseSimplePrice(PoecurrencyRawItem raw)
    {
        var result = ChoosePairPrice(raw.LatestBuy1, raw.LatestSell1, "latest_buy1", "latest_sell1");
        if (result.price > 0)
        {
            return result.price;
        }

        return GeometricMean(raw.BuyAverage, raw.SellAverage);
    }

    private static (decimal price, string source) ChoosePairPrice(
        decimal buy,
        decimal sell,
        string buyField,
        string sellField)
    {
        if (buy > 0 && sell > 0)
        {
            var ratio = SpreadRatio(buy, sell);
            if (ratio <= 5)
            {
                return (GeoMean(buy, sell), $"geo_{buyField}_{sellField}");
            }

            return buy <= sell
                ? (buy, $"{buyField}_conservative_spread_gt_5x")
                : (sell, $"{sellField}_conservative_spread_gt_5x");
        }

        if (sell > 0)
        {
            return (sell, $"{sellField}_only");
        }

        if (buy > 0)
        {
            return (buy, $"{buyField}_only");
        }

        return (0, "");
    }

    private static (decimal price, string source) ChoosePairPriceWithReference(
        decimal buy,
        decimal sell,
        string buyField,
        string sellField,
        decimal reference,
        string referenceField)
    {
        if (buy > 0 && sell > 0)
        {
            var ratio = SpreadRatio(buy, sell);
            if (ratio > 5 && reference > 0)
            {
                var closest = ClosestToReference(reference, (buy, buyField), (sell, sellField));
                if (closest.price > 0)
                {
                    return (closest.price, $"{closest.source}_closest_to_{referenceField}_spread_gt_5x");
                }
            }
        }

        return ChoosePairPrice(buy, sell, buyField, sellField);
    }

    private static (decimal price, string source) TryDigitShiftedDivinePrice(PoecurrencyRawItem raw)
    {
        if (raw.LatestBuy1 <= 0 || raw.LatestSell1 <= 0)
        {
            return (0, "");
        }

        var high = Math.Max(raw.LatestBuy1, raw.LatestSell1);
        var low = Math.Min(raw.LatestBuy1, raw.LatestSell1);

        if (low == Math.Truncate(low))
        {
            return (0, "");
        }

        var ratio = SpreadRatio(high, low);
        if (ratio < 20 || ratio > 200)
        {
            return (0, "");
        }

        if (high < 50 || high > 1000)
        {
            return (0, "");
        }

        var scaledHigh = high / 100;
        if (SpreadRatio(low, scaledHigh) > 5)
        {
            return (0, "");
        }

        var highField = raw.LatestBuy1 >= raw.LatestSell1 ? "latest_buy1" : "latest_sell1";
        var lowField = raw.LatestBuy1 >= raw.LatestSell1 ? "latest_sell1" : "latest_buy1";
        return (GeoMean(low, scaledHigh), $"geo_{lowField}_{highField}_d_digit_shift_100x");
    }

    private static (decimal price, string source) ClosestToReference(
        decimal reference,
        params (decimal price, string source)[] candidates)
    {
        var positive = candidates.Where(c => c.price > 0).ToList();
        if (positive.Count == 0)
        {
            return (0, "");
        }

        if (reference <= 0)
        {
            return positive.OrderByDescending(c => c.price).First();
        }

        return positive
            .OrderBy(c => SpreadRatio(c.price, reference))
            .First();
    }

    private static string GetUnit(PoecurrencyRawItem raw)
    {
        var rawUnit = raw.CurrencyUnit?.Trim().ToLowerInvariant() ?? "";
        if (rawUnit is "d" or "divine" or "divine orb" or "divine_orb" or "神圣石" or "神圣宝珠")
        {
            return "d";
        }

        if (rawUnit is "e" or "exalted" or "exalted orb" or "exalted_orb" or "崇高石" or "崇高宝珠")
        {
            return "e";
        }

        return "e";
    }

    private static bool IsDivine(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace(" ", "")
            .Replace("\u3000", "");
        return normalized is "神圣石" or "神圣宝珠" or "divineorb" or "divine";
    }

    private static decimal GeometricMean(decimal a, decimal b)
    {
        if (a > 0 && b > 0)
        {
            return (decimal)Math.Sqrt((double)(a * b));
        }

        if (a > 0)
        {
            return a;
        }

        if (b > 0)
        {
            return b;
        }

        return 0;
    }

    private static string GeometricMeanSource(decimal buy, decimal sell)
    {
        if (buy > 0 && sell > 0)
        {
            return "geo_buy_avg_sell_avg";
        }

        if (buy > 0)
        {
            return "buy_avg_only";
        }

        if (sell > 0)
        {
            return "sell_avg_only";
        }

        return "avg_unavailable";
    }

    private static decimal SpreadRatio(decimal left, decimal right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }

        return Math.Max(left, right) / Math.Min(left, right);
    }

    private static decimal GeoMean(decimal a, decimal b)
    {
        return (decimal)Math.Sqrt((double)(a * b));
    }

    #region Json Helpers

    private static JsonElement FirstPresent(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                return value;
            }
        }

        return default;
    }

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

    private static bool GetBool(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            var text = value.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (text is "1" or "true" or "yes")
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    private sealed class PoecurrencyCategory
    {
        public string Label { get; set; } = "";
        public List<PoecurrencyRawItem> Items { get; set; } = [];
    }

    private sealed class PoecurrencyRawItem
    {
        public string Name { get; set; } = "";
        public decimal LatestBuy1 { get; set; }
        public decimal LatestSell1 { get; set; }
        public decimal BuyAverage { get; set; }
        public decimal SellAverage { get; set; }
        public decimal BuyAverageYesterday { get; set; }
        public decimal SellAverageYesterday { get; set; }
        public decimal PreviousBuy1 { get; set; }
        public string CurrencyUnit { get; set; } = "";
        public decimal ExplicitExalted { get; set; }
        public bool HasError { get; set; }
        public string ErrorInfo { get; set; } = "";
    }
}
