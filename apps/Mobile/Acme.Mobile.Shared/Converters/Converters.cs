using System.Globalization;

namespace Acme.Mobile.Shared.Converters;

/// <summary>反转布尔值。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>非空 → true（用于可见性绑定）。字符串空串也算“空”。
/// 传 ConverterParameter="inverse" 则反转（空 → true），用于二者择一的互斥可见性。</summary>
public class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool notEmpty = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        return parameter is string p && p.Equals("inverse", StringComparison.OrdinalIgnoreCase) ? !notEmpty : notEmpty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空非空串 → true。</summary>
public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（MAUI 用 IsVisible，直接返回 bool；此转换器留作占位/反转组合）。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
}

/// <summary>取字符串首字符大写作为头像占位（空串返回 "?"）。</summary>
public class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? "?" : char.ToUpper(s[0]).ToString();
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
