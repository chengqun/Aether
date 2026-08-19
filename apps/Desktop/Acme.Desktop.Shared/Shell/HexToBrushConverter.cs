using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>hex 字符串 → SolidColorBrush（用于色块预览/边框 Background 绑 PaletteColorRow.Hex）。
/// 解析失败返透明（不崩）。单向（色块只显不回写）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Color.TryParse(s, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
