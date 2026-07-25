using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell.Converters;

public class DictToStringConverter : IValueConverter
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        if (value is Dictionary<string, object> dict)
        {
            if (dict.Count == 0)
                return string.Empty;

            try
            {
                return JsonSerializer.Serialize(dict, _options);
            }
            catch
            {
                return dict.ToString() ?? string.Empty;
            }
        }

        return value.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
