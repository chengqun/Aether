using System.Globalization;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>true → 编辑预览，false → 预览模式（按钮文字切换）。</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter EditPreview = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? "✏️ 编辑" : "👁 预览";
        return "👁 预览";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
