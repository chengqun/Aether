using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>variant 字符串 ↔ RadioButton IsChecked 双向转换。
/// Convert: value=Variant("light"/"dark"), parameter=目标项 → 相等返 true。
/// ConvertBack: IsChecked=true → 返回 parameter（写回 Variant）；IsChecked=false → null（不动，避免互相清零）。
/// PaletteWindow 明暗基底 tab 两 RadioButton 用。静态单例 Instance。</summary>
public sealed class VariantToBoolConverter : IValueConverter
{
    public static readonly VariantToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value as string;
        var p = parameter as string;
        return !string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(p) && string.Equals(v, p, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只有变 true 的那个 RadioButton 回写其 parameter；返 null 让另一项保持（标准 RadioButton 互斥写法）。
        return value is true ? (parameter as string) : null;
    }
}
