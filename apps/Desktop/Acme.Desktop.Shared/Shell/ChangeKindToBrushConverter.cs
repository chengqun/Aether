using System;
using System.Globalization;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>变更类型 → 状态字母色（语义色，非主题 token——连接/涨跌色同理保留硬编码）。</summary>
public sealed class ChangeKindToBrushConverter : IValueConverter
{
    public static readonly ChangeKindToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChangeKind k ? BrushFor(k) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush BrushFor(ChangeKind k) => k switch
    {
        ChangeKind.Modified => Brushes.Orange,
        ChangeKind.Added => Brushes.Green,
        ChangeKind.Deleted => Brushes.IndianRed,
        ChangeKind.Untracked => Brushes.Gray,
        ChangeKind.Renamed => Brushes.DodgerBlue,
        _ => Brushes.Gray,
    };
}
