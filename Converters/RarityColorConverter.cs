using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 根据物品稀有度返回对应颜色：传奇=橙，稀有=黄，魔法=蓝，普通=白。
/// </summary>
public class RarityColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isUnique && isUnique)
        {
            return new SolidColorBrush(Color.FromRgb(0xAF, 0x60, 0x22)); // 传奇橙
        }
        return new SolidColorBrush(Color.FromRgb(0xFF, 0xE5, 0x66)); // 稀有黄
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
