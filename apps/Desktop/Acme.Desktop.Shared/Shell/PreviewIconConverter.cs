using System.Globalization;
using Acme.Shell;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>IsPreviewMode → 矢量图标 path data：预览中显示 EyeOff（点回编辑），编辑中显示 Eye（进预览）。</summary>
public sealed class PreviewIconConverter : IValueConverter
{
    public static readonly PreviewIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? NavIcons.EyeOff : NavIcons.Eye;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
