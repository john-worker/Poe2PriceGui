using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 将 List&lt;string&gt; 拼接为逗号分隔的字符串。
/// </summary>
public class StringJoinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable enumerable)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? "");
            }
            return string.Join(", ", items.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
