using System.Globalization;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>判断对象不为 null。</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
