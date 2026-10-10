using System.Globalization;
using Acme.Ctp.Domain;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// 持仓方向显示：Long→多 / Short→空 / Net→净（国内期货习惯）。默认返回文本；
/// <c>ConverterParameter="brush"</c> 返回配色（多红/空绿/净灰——与 <see cref="ChangePercentBrushConverter"/> 同色板）。
/// </summary>
public sealed class PosiDirectionTextConverter : IValueConverter
{
    public static readonly PosiDirectionTextConverter Instance = new();

    private static readonly IBrush LongBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly IBrush ShortBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly IBrush FlatBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var wantBrush = parameter as string == "brush";
        if (value is not CtpPosiDirection d)
            return wantBrush ? FlatBrush : "";
        return (d, wantBrush) switch
        {
            (CtpPosiDirection.Long, true) => LongBrush,
            (CtpPosiDirection.Short, true) => ShortBrush,
            (_, true) => FlatBrush,
            (CtpPosiDirection.Long, false) => "多",
            (CtpPosiDirection.Short, false) => "空",
            (_, false) => "净",
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
