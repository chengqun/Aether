using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>活动热力图强度 0-4 → accent 紫 5 档浓淡 brush（半透明 accent 在 light/dark 底色上都可见；
/// L0=Transparent 显示容器底色=无活动）。照 <c>LaneToBrushConverter</c> 硬编码 uint palette 范式，零主题 API 依赖。
/// int 越界 → Transparent。</summary>
public sealed class IntensityToBrushConverter : IValueConverter
{
    public static readonly IntensityToBrushConverter Instance = new();

    private static readonly IBrush[] Palette =
    {
        Brushes.Transparent,                                        // L0 无活动
        new SolidColorBrush(Color.FromArgb(0x30, 0x5B, 0x5F, 0xC7)), // L1 accent 19%
        new SolidColorBrush(Color.FromArgb(0x60, 0x5B, 0x5F, 0xC7)), // L2 accent 38%
        new SolidColorBrush(Color.FromArgb(0xA0, 0x5B, 0x5F, 0xC7)), // L3 accent 63%
        new SolidColorBrush(Color.FromUInt32(0xFF5B5FC7)),           // L4 accent 100%
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i >= 0 && i < Palette.Length ? Palette[i] : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
