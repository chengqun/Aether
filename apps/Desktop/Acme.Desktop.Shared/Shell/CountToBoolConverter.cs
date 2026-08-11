using System.Globalization;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>绑定的数值大于 0 时返回 true（用于把 ItemCount 折成 HasChildren 布尔，控制叶子节点是否显示展开箭头）。</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? i > 0 : value is long l && l > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
