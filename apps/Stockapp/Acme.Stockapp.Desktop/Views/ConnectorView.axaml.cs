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

    /// <summary>打开 CTP 连接设置对话框。</summary>
    private async void OnCtpSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CtpConnectorViewModel vm) return;
        var dialog = new CtpLoginWindow { DataContext = vm };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
    }
}
