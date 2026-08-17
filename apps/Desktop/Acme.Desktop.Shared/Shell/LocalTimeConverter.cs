using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>DateTimeOffset（stored UTC +00）→ 中国时区（UTC+8）"HH:mm" 字符串。
/// 平台层 started_at/finished_at/When 存 UTC round-trip ISO（offset +00:00），StringFormat "HH:mm" 只格式化 offset 时刻
/// → 显示 UTC 小时（比中国晚 8 小时）。先 <see cref="DateTimeOffset.ToOffset"/> 转 +8 再格式化得中国时间。
/// 硬编码 +8（不依赖系统时区——出差/海外开发机也准）。</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    /// <summary>中国标准时区偏移（UTC+8）。</summary>
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public static readonly LocalTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
            return dto.ToOffset(ChinaOffset).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (value is DateTime dt)
            return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
