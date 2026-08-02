using System;
using System.Threading.Tasks;
using Acme.Desktop.Shared.Shell;
using Acme.Shell;
using Avalonia;
using Avalonia.Controls;
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
        RestoreLayout();
        // IsVisible 切换不会触发 Unloaded，改钩父窗口 Closing 保存最终分隔位置
        if (!_closingHooked && TopLevel.GetTopLevel(this) is Window win)
        {
            _closingHooked = true;
            win.Closing += (_, _) => SaveLayout();
        }
    }

    private static IPreferencesStore? Store => DesktopApp.Services.GetService<IPreferencesStore>();

    /// <summary>恢复回测参数栏宽度。</summary>
    private void RestoreLayout()
    {
        var store = Store;
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("trading.configW").GetAwaiter().GetResult(), out var w)
                && w is >= 240 and <= 720)
                TradingSplitGrid.ColumnDefinitions[0].Width = new GridLength(w);
        }
        catch { /* 读取失败用默认宽度 */ }
    }

    /// <summary>关闭时持久化回测参数栏宽度（后台线程写文件，Wait 确保落盘）。</summary>
    private void SaveLayout()
    {
        var store = Store;
        if (store is null) return;
        double w = TradingSplitGrid.ColumnDefinitions[0].Width.Value;
        try
        {
            Task.Run(async () => await store.SetAsync("trading.configW", w.ToString("0.#"))).Wait();
        }
        catch { /* 关闭期写入失败忽略 */ }
    }
}
