using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>lane 列号 → 调色板色（commit 列表行的色点）。palette 与 CommitGraphBuilder/HistoryView 同步。</summary>
public class LaneToBrushConverter : IValueConverter
{
    public static readonly LaneToBrushConverter Instance = new();

    private static readonly uint[] Palette =
    {
        0xFF42A5F5, 0xFF66BB6A, 0xFFAB47BC, 0xFFFFA726,
        0xFF26C6DA, 0xFFEF5350, 0xFFD4E157, 0xFF8D6E63,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int lane)
            return new SolidColorBrush(Color.FromUInt32(Palette[((lane % 8) + 8) % 8]));
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
