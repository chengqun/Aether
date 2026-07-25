using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell.Converters;

public class BoolToConnectionBrushConverter : IValueConverter
{
    public static readonly BoolToConnectionBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b
            ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
