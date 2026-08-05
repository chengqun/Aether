using System;
using System.Threading.Tasks;
using Acme.Desktop.Shared.Shell;
using Acme.Shell;
using Acme.Stockapp.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop.Views;

public partial class TradingView : UserControl
{
    private bool _closingHooked;

    public TradingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // 回测配置已改为弹窗，不再需要布局持久化
    }

    /// <summary>弹出回测配置窗口，配置 + 运行回测。</summary>
    private async void OnBacktestConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TradingViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new BacktestConfigWindow(vm);
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    private static IPreferencesStore? Store => DesktopApp.Services.GetService<IPreferencesStore>();

    // 回测配置栏已改为弹窗，不再需要 GridSplitter 布局持久化
}
