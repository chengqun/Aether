using System;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

public sealed class MaskApiKeyConverter : IValueConverter
{
    public static MaskApiKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string apiKey && !string.IsNullOrEmpty(apiKey))
        {
            if (apiKey.Length <= 8)
                return "******";
            return apiKey[..4] + "..." + apiKey[^4..];
        }
        return "-";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}