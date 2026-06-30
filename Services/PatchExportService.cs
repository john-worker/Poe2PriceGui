using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 将当前价格数据导出为补丁流程可用的 CSV/JSON 文件。
/// </summary>
public class PatchExportService
{
    private readonly string _outputDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PatchExportService()
    {
        _outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    public string OutputDirectory => _outputDirectory;
    public string PricesCsvPath => Path.Combine(_outputDirectory, "prices.csv");
    public string EditedPricesJsonPath => Path.Combine(_outputDirectory, "edited_prices.json");

    /// <summary>
    /// 导出所有当前显示的价格为 prices.csv，供 poe2_name_price_patch.py 使用。
    /// </summary>
    public async Task<int> ExportPricesCsvAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var priceList = prices.ToList();
        var globalDivineRatio = priceList
            .FirstOrDefault(p => IsDivineOrbName(p.ItemName))?
            .PriceExalted ?? 0;

        var rows = priceList
            .Where(p => p.PriceExalted >= 1)
            .Select(p => new CsvRow
            {
                MetadataPath = "",
                Name = p.ItemName,
                Price = FormatPrice(p, globalDivineRatio),
                NewName = "",
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("metadata_path,name,price,new_name");
        foreach (var row in rows)
        {
            sb.AppendLine($"{Escape(row.MetadataPath)},{Escape(row.Name)},{Escape(row.Price)},{Escape(row.NewName)}");
        }

        await File.WriteAllTextAsync(PricesCsvPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        AppLogger.Instance.Info($"导出 prices.csv：{rows.Count} 条，路径：{PricesCsvPath}");
        return rows.Count;
    }

    /// <summary>
    /// 导出用户手动编辑过的价格为 edited_prices.json，便于核对与回滚。
    /// </summary>
    public async Task<int> ExportEditedPricesJsonAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var edited = prices
            .Where(p => p.IsEdited)
            .Select(p => new
            {
                p.CategoryLabel,
                p.ItemName,
                p.PriceExalted,
                p.CurrencyUnit,
                p.HasError,
                p.ErrorInfo,
            })
            .ToList();

        await File.WriteAllTextAsync(
            EditedPricesJsonPath,
            JsonSerializer.Serialize(edited, _jsonOptions),
            cancellationToken);

        AppLogger.Instance.Info($"导出 edited_prices.json：{edited.Count} 条，路径：{EditedPricesJsonPath}");
        return edited.Count;
    }

    private static string FormatPrice(PoecurrencyItem item, decimal globalDivineRatio)
    {
        var ratio = item.DivineExaltedRatio > 0 ? item.DivineExaltedRatio : globalDivineRatio;

        decimal value;
        string unit;
        if (ratio > 0 && item.PriceExalted >= ratio)
        {
            value = item.PriceExalted / ratio;
            unit = "d";
        }
        else
        {
            value = item.PriceExalted;
            unit = "e";
        }

        // e 单位用整数，d 单位保留最多 4 位小数并去掉末尾无意义的 0。
        var format = unit == "e" ? "0" : "0.##";
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        return $"[{text}{unit}]";
    }

    private static bool IsDivineOrbName(string name)
    {
        return name.Trim().ToLowerInvariant() is "神圣石" or "神圣宝珠" or "divine orb" or "divine";
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private sealed class CsvRow
    {
        public string MetadataPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string Price { get; set; } = "";
        public string NewName { get; set; } = "";
    }
}
