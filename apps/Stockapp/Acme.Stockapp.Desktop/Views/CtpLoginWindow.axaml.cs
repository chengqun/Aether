using Acme.Desktop.Shared.Shell;
using Acme.Stockapp.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

public partial class CtpLoginWindow : ThemedWindow
{
    public CtpLoginWindow()
    {
        InitializeComponent();
    }

    /// <summary>连接：调 ConnectCommand，连接成功才关窗。</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CtpConnectorViewModel vm) return;
        await vm.ConnectCommand.ExecuteAsync(null);
        if (vm.IsConnected) Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>密码眼睛：掩码 ↔ 明文（Avalonia 12 无 RevealPassword——PasswordChar 置 '\0' 即不掩码；纯视图层）。</summary>
    private void OnPasswordEyeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
            PasswordBox.PasswordChar = tb.IsChecked == true ? '\0' : '•';
    }

    /// <summary>标签点击 = 应用该 profile 并直接登录（VM 内部先登出再登录）；成功才关窗（同 OnConnectClick 语义）。</summary>
    private async void OnProfileChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CtpProfileItem item } && DataContext is CtpConnectorViewModel vm)
        {
            await vm.ApplyProfileCommand.ExecuteAsync(item);
            if (vm.IsConnected) Close();
        }
    }
}
