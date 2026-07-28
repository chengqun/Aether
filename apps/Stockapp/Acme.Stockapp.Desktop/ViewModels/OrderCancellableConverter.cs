using System.Globalization;
using Acme.Ctp.Domain;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>订单是否可撤。CTP OrderStatus: '1'部分成交排队 '2'未成交 '3'排队 '4'单据已报 'a'未知 'b'条件单未触发——可撤。</summary>
public sealed class OrderCancellableConverter : IValueConverter
{
    public static readonly OrderCancellableConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CtpOrder o) return false;
        char s = (char)o.OrderStatusRaw;
        return s is '1' or '2' or '3' or '4' or 'a' or 'b';
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
