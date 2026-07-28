using Acme.Stockapp.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

public partial class FuturesView : UserControl
{
    public FuturesView()
    {
        InitializeComponent();
    }

    /// <summary>打开 CTP 连接设置对话框（复用 CtpLoginWindow，绑定到 CtpViewModel.Connector）。</summary>
    private async void OnCtpSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CtpViewModel vm) return;
        var dialog = new CtpLoginWindow { DataContext = vm.Connector };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
    }
}
