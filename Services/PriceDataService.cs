using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 本地价格数据持久化服务：启动时优先加载本地数据，刷新/编辑后自动保存。
/// </summary>
public class PriceDataService
{
    private readonly string _dataDirectory;
    private readonly string _dataFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public PriceDataService()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        _dataFilePath = Path.Combine(_dataDirectory, "prices.json");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    /// <summary>
    /// 本地数据文件是否存在。
    /// </summary>
    public bool LocalDataExists => File.Exists(_dataFilePath);

    /// <summary>
    /// 本地数据文件路径。
    /// </summary>
    public string DataFilePath => _dataFilePath;

    /// <summary>
    /// 从本地 JSON 加载价格数据。
    /// </summary>
    public async Task<List<PoecurrencyItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_dataFilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_dataFilePath);
        var data = await JsonSerializer.DeserializeAsync<PriceData>(stream, _jsonOptions, cancellationToken);

        if (data?.Prices == null || data.Prices.Count == 0)
        {
            return [];
        }

        var globalDivineRatio = data.Prices
            .FirstOrDefault(r => IsDivineOrbName(r.ItemName) && r.PriceExalted > 0)?
            .PriceExalted ?? 0;

        return data.Prices.Select(record => new PoecurrencyItem
        {
            CategoryLabel = record.CategoryLabel,
            ItemName = record.ItemName,
            IconUrl = record.IconUrl,
            LatestBuy1 = record.LatestBuy1,
            LatestSell1 = record.LatestSell1,
            BuyAverage = record.BuyAverage,
            SellAverage = record.SellAverage,
            PreviousBuy1 = record.PreviousBuy1,
            CurrencyUnit = string.IsNullOrWhiteSpace(record.CurrencyUnit) ? "e" : record.CurrencyUnit,
            HasError = record.HasError,
            ErrorInfo = record.ErrorInfo,
            PriceExalted = record.PriceExalted,
            DivineExaltedRatio = record.DivineExaltedRatio > 0 ? record.DivineExaltedRatio : globalDivineRatio,
            SourcePair = record.SourcePair,
            IsEdited = record.IsEdited,
        }).ToList();
    }

    /// <summary>
    /// 将价格数据保存到本地 JSON。
    /// </summary>
    public async Task SaveAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);

        var records = prices.Select(item => new PriceRecord
        {
            CategoryLabel = item.CategoryLabel,
            ItemName = item.ItemName,
            IconUrl = item.IconUrl,
            LatestBuy1 = item.LatestBuy1,
            LatestSell1 = item.LatestSell1,
            BuyAverage = item.BuyAverage,
            SellAverage = item.SellAverage,
            PreviousBuy1 = item.PreviousBuy1,
            CurrencyUnit = item.CurrencyUnit,
            HasError = item.HasError,
            ErrorInfo = item.ErrorInfo,
            PriceExalted = item.PriceExalted,
            DivineExaltedRatio = item.DivineExaltedRatio,
            SourcePair = item.SourcePair,
            IsEdited = item.IsEdited,
        }).ToList();

        var data = new PriceData
        {
            SavedAt = DateTime.Now,
            Prices = records,
        };

        var tempPath = _dataFilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(data, _jsonOptions), cancellationToken);

        if (File.Exists(_dataFilePath))
        {
            File.Delete(_dataFilePath);
        }

        File.Move(tempPath, _dataFilePath);
    }

    private sealed class PriceData
    {
        public DateTime SavedAt { get; set; }
        public List<PriceRecord> Prices { get; set; } = [];
    }

    private sealed class PriceRecord
    {
        public string CategoryLabel { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string? IconUrl { get; set; }
        public decimal LatestBuy1 { get; set; }
        public decimal LatestSell1 { get; set; }
        public decimal BuyAverage { get; set; }
        public decimal SellAverage { get; set; }
        public decimal PreviousBuy1 { get; set; }
        public string CurrencyUnit { get; set; } = "e";
        public bool HasError { get; set; }
        public string ErrorInfo { get; set; } = "";
        public decimal PriceExalted { get; set; }
        public decimal DivineExaltedRatio { get; set; }
        public string SourcePair { get; set; } = "";
        public bool IsEdited { get; set; }
    }

    private static bool IsDivineOrbName(string name)
    {
        return name.Trim().ToLowerInvariant() is "神圣石" or "神圣宝珠" or "divine orb" or "divine";
    }
}
