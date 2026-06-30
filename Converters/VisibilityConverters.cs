using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 非空字符串 → Visible，空/null → Collapsed。
/// </summary>
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 非零数字 → Visible，0/null → Collapsed。
/// </summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null) return Visibility.Collapsed;
        return value switch
        {
            int i => i > 0 ? Visibility.Visible : Visibility.Collapsed,
            double d => d > 0 ? Visibility.Visible : Visibility.Collapsed,
            decimal dec => dec > 0 ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
