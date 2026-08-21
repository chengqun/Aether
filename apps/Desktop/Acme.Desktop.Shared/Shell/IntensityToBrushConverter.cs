using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Acme.Desktop.Shared.Shell;

/// <summary>活动热力图强度 0-4 → GitHub 贡献图实际绿色色阶 brush。
/// 取 github.com 用户贡献图的官方色阶——浅色主题 L1-L4 = #9be9a8/#40c463/#30a14e/#216e39，
/// 深色主题 L1-L4 = #0e4429/#006d32/#26a641/#39d353——跟随控件 <see cref="Control.ActualThemeVariant"/>
/// 在两套色阶间切换（与 github.com 一致：浅模式浅绿、深模式深绿）。
/// L0=Transparent 显示容器底色=无活动（不画格，比 GitHub 的 #ebedf0/#161b22 空格更干净，
/// 且自动适配自定义主题的 card bg）。
/// <para><see cref="IMultiValueConverter"/> 而非 <see cref="IValueConverter"/>：第二个绑定项是
/// <c>Border.ActualThemeVariant</c>，主题切换时该属性变更触发 MultiBinding 重算，格子即时换色——
/// 单值 converter 只在 Intensity 变时重算、切主题不会刷新。</para>
/// values[0]=intensity(int)，values[1]=ThemeVariant；越界或缺参 → Transparent。</summary>
public sealed class IntensityToBrushConverter : IMultiValueConverter
{
    public static readonly IntensityToBrushConverter Instance = new();

    // GitHub 贡献图浅色主题色阶（github.com light）。FromUInt32 按 #AARRGGBB（Avalonia 约定）。
    private static readonly IBrush[] LightPalette =
    {
        Brushes.Transparent,                               // L0 无活动（容器底）
        new SolidColorBrush(Color.FromUInt32(0xFF9BE9A8)), // L1
        new SolidColorBrush(Color.FromUInt32(0xFF40C463)), // L2
        new SolidColorBrush(Color.FromUInt32(0xFF30A14E)), // L3
        new SolidColorBrush(Color.FromUInt32(0xFF216E39)), // L4
    };

    // GitHub 贡献图深色主题色阶（github.com dark）。
    private static readonly IBrush[] DarkPalette =
    {
        Brushes.Transparent,                               // L0
        new SolidColorBrush(Color.FromUInt32(0xFF0E4429)), // L1
        new SolidColorBrush(Color.FromUInt32(0xFF006D32)), // L2
        new SolidColorBrush(Color.FromUInt32(0xFF26A641)), // L3
        new SolidColorBrush(Color.FromUInt32(0xFF39D353)), // L4
    };

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is { Count: >= 2 } && values[0] is int i && i >= 0 && i < LightPalette.Length)
        {
            var dark = values[1] is ThemeVariant tv && tv == ThemeVariant.Dark;
            return dark ? DarkPalette[i] : LightPalette[i];
        }
        return Brushes.Transparent;
    }
}
