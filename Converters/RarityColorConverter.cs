using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 根据物品稀有度返回对应颜色（参考 xiletrade 配色）。
/// 输入：Rarity 字符串（如 "Unique"/"传奇"/"Rare"/"稀有"/"Magic"/"魔法"/"Normal"/"普通"）。
/// 传奇=#AF6025，稀有=#FFFF77，魔法=#8888FF，普通=#C8C8C8。
/// 兼容旧用法：传入 bool（true=传奇，false=稀有）。
/// </summary>
public class RarityColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 兼容旧用法：传入 bool（IsUnique）。
        if (value is bool isUnique)
        {
            return isUnique
                ? new SolidColorBrush(Color.FromRgb(0xAF, 0x60, 0x25)) // 传奇橙
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x77)); // 稀有黄
        }

        // 新用法：传入 Rarity 字符串。
        var rarity = value as string ?? "";
        if (rarity.Contains("Unique", StringComparison.OrdinalIgnoreCase)
            || rarity.Contains("传奇", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0xAF, 0x60, 0x25)); // UniqueWebColor
        }
        if (rarity.Contains("Rare", StringComparison.OrdinalIgnoreCase)
            || rarity.Contains("稀有", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x77)); // RareWebColor
        }
        if (rarity.Contains("Magic", StringComparison.OrdinalIgnoreCase)
            || rarity.Contains("魔法", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xFF)); // MagicWebColor
        }
        // Normal / 普通 / 其它
        return new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)); // NormalWebColor
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
