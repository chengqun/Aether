using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 工作台薄 host：可展开侧栏 + 工作台主区 ContentControl。
/// 不决定布局——布局由 <see cref="WorkspaceView"/> 按 ActiveWorkspace.Layout 构建。
/// 主题：订阅 VM 的 Theme/CustomThemeJson 变化 → <see cref="ThemeLoader.Apply"/> 叠加 JSON overlay。
/// 窗口尺寸持久化到 IPreferencesStore（ui.winW/winH），跨重启恢复；Trace 窗经 account flyout 打开。
/// </summary>
public partial class WorkbenchWindow : Window
{
    private WorkbenchShellViewModel? _vm;

    public WorkbenchWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RestoreWindowSize(); // 构造期同步恢复，窗口尚未显示 → 不闪烁
    }

    private static IPreferencesStore? PreferencesStore => DesktopApp.Services.GetService<IPreferencesStore>();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as WorkbenchShellViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 初始主题应用（若有自定义 JSON）。
        ThemeLoader.Apply(this, _vm?.CustomThemeJson);
        ApplyThemeVariant();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        PersistWindowSize();
    }

    /// <summary>打开运行 Trace 查看窗口（OTel agent.run span）。</summary>
    public void OnOpenTrace(object? sender, RoutedEventArgs e)
    {
        if (_vm is { Trace: { } trace })
        {
            var window = new TraceWindow { DataContext = trace };
            window.Show();
        }
    }

    /// <summary>账号菜单「🎨 主题」子项点击：MenuItem.Click 从叶子子项冒泡到父「🎨 主题」，
    /// <c>e.Source</c> 的 DataContext 是被点的 ThemeOption。父项自身 DataContext 是 shell VM（非 ThemeOption）
    /// → 自动跳过。经 ThemeOption.SelectCommand 触发换肤（避开 MenuItem 容器 Command 绑定的跨 popup 歧义）。</summary>
    public void OnSelectTheme(object? sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem mi && mi.DataContext is ThemeOption t)
            t.SelectCommand?.Execute(t.Name);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchShellViewModel.Theme)
            or nameof(WorkbenchShellViewModel.CustomThemeJson))
        {
            ThemeLoader.Apply(this, _vm?.CustomThemeJson);
        }
        if (e.PropertyName == nameof(WorkbenchShellViewModel.IsDarkVariant))
            ApplyThemeVariant();
    }

    /// <summary>把主题明暗倾向同步到 Avalonia 原生控件主题。<see cref="ThemeLoader"/> 的 JSON overlay
    /// 只覆盖自定义 token 画刷；原生控件（TextBox/ComboBox/滚动条/ListBox…）须经 <see cref="ThemeVariant"/>
    /// 翻转，否则 dark 主题下原生控件仍是浅底白块。设到 Application 级——所有子窗口（设置/Trace…）一并翻转。</summary>
    private void ApplyThemeVariant()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = (_vm?.IsDarkVariant ?? false) ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // —— 窗口尺寸持久化（布局/分隔条由 WorkspaceView + WorkbenchShellViewModel 跨重启持久） ——

    /// <summary>构造期同步恢复窗口尺寸（JsonPreferencesStore.GetAsync 同步完成）。</summary>
    private void RestoreWindowSize()
    {
        var store = PreferencesStore;
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("ui.winW").GetAwaiter().GetResult(), out var w) && w >= 900)
                Width = w;
            if (double.TryParse(store.GetAsync("ui.winH").GetAwaiter().GetResult(), out var h) && h >= 600)
                Height = h;
        }
        catch { /* 读取失败用默认尺寸 */ }
    }

    /// <summary>关闭时持久化窗口尺寸。后台线程写文件（避免 UI 线程 await 死锁），Wait 确保关闭前落盘。</summary>
    private void PersistWindowSize()
    {
        var store = PreferencesStore;
        if (store is null) return;
        double winW = Width, winH = Height;
        try
        {
            Task.Run(async () =>
            {
                await store.SetAsync("ui.winW", winW.ToString("0.#"));
                await store.SetAsync("ui.winH", winH.ToString("0.#"));
            }).Wait();
        }
        catch { /* 关闭期写入失败忽略 */ }
    }
}
