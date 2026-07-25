using System.Globalization;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>值不等于 ConverterParameter 时返回 true（用于类别 Tab 的 IsEnabled）。</summary>
public sealed class IsNotEqualConverter : IValueConverter
{
    public static readonly IsNotEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is { } p)
            return !Equals(value, p);
        return value != null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
