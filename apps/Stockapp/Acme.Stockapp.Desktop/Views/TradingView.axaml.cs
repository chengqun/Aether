using System;
using System.Threading.Tasks;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Mvvm;
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
        // 回测配置在 TabControl 第一个 tab，不再需要弹窗/布局持久化
    }

    private static IPreferencesStore? Store => DesktopApp.Services.GetService<IPreferencesStore>();

    // 回测配置栏已改为弹窗，不再需要 GridSplitter 布局持久化
}
