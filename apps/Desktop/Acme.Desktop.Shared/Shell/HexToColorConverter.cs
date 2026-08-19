using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>hex 字符串（#RRGGBB，VM/JSON 侧）↔ Avalonia.Media.Color（ColorView/ColorPicker 控件侧）双向转换。
/// 让 PaletteColorRow.Hex（string）绑到 ColorPicker.Color（Color）：拖色板→Hex，Hex→色块。</summary>
public sealed class HexToColorConverter : IValueConverter
{
    public static readonly HexToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Color.TryParse(s, out var c) ? c : Colors.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;
}
