using Acme.Stockapp.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

public partial class CtpLoginWindow : Window
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
}
