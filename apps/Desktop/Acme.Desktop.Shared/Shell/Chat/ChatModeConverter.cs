using System.Globalization;
using Acme.Desktop.Mvvm;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>ComboBox SelectedItem 双向转换：<see cref="ChatModeInfo"/>（条目）↔ <see cref="ChatMode"/>（VM 枚举）。
/// Avalonia ComboBox 无 SelectedValuePath/SelectedValue（WPF 才有），故用 SelectedItem + 本转换器桥接。</summary>
public sealed class ChatModeConverter : IValueConverter
{
    public static readonly ChatModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChatMode m ? ChatModes.All.FirstOrDefault(i => i.Mode == m) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChatModeInfo info ? info.Mode : Avalonia.Data.BindingOperations.DoNothing;
}
