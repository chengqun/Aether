using Acme.Stockapp.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

public partial class ConnectorView : UserControl
{
    public ConnectorView()
    {
        InitializeComponent();
    }

    /// <summary>打开 CTP 连接设置对话框（CTP 卡片 DataContext 是复合连接器的 Ctp 子 VM）。</summary>
    private async void OnCtpSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StockappConnectorViewModel vm) return;
        var dialog = new CtpLoginWindow { DataContext = vm.Ctp };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
    }

    /// <summary>打开同花顺连接设置对话框（exe 路径 / 窗口标题）。</summary>
    private async void OnThsSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StockappConnectorViewModel vm) return;
        var dialog = new ThsSettingsWindow { DataContext = vm.Ths };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
    }
}
