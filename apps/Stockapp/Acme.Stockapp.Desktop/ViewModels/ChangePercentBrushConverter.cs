using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>涨幅配色：&gt;0 红（涨）、&lt;0 绿（跌）、=0 灰。国内期货习惯红涨绿跌。</summary>
public sealed class ChangePercentBrushConverter : IValueConverter
{
    public static readonly ChangePercentBrushConverter Instance = new();

    private static readonly IBrush Up = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly IBrush Down = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly IBrush Flat = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d > 0 ? Up : d < 0 ? Down : Flat;
        return Flat;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
