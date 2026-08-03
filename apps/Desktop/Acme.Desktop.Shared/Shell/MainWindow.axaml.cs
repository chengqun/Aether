using System;
using System.Threading.Tasks;
using Acme.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌面 shell 主窗口（两租户共用）。展开/收起两套侧边栏在 XAML 里靠 IsVisible 切换，
/// 无业务逻辑。DataContext = 租户 MainViewModel（: MainViewModelBase）。
/// 窗口尺寸 / 聊天栏宽度 / 笔记分隔位置持久化到 IPreferencesStore，跨重启恢复。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowSize(); // 构造期同步恢复，窗口尚未显示 → 不闪烁
        RootGrid.SizeChanged += (_, _) => ClampChatColumn(); // 窗口缩放时别让 chat 列撑出可视区
    }

    private static IPreferencesStore? PreferencesStore => DesktopApp.Services.GetService<IPreferencesStore>();

    /// <summary>打开运行 Trace 查看窗口（OTel agent.run span）。</summary>
    public void OnOpenTrace(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModelBase vm && vm.Trace is { } trace)
        {
            var window = new TraceWindow { DataContext = trace };
            window.Show();
        }
    }

    // —— 布局持久化 ——

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreChatColumn();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        PersistLayout();
    }

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

    /// <summary>窗口打开后恢复聊天栏宽度。</summary>
    private void RestoreChatColumn()
    {
        var store = PreferencesStore;
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("ui.chatW").GetAwaiter().GetResult(), out var cw)
                && cw >= 300)
                RootGrid.ColumnDefinitions[2].Width = new GridLength(cw);
            ClampChatColumn(); // 恢复的宽度可能超出当前窗口可用宽度 → 收一下
        }
        catch { /* 读取失败用默认宽度 */ }
    }

    /// <summary>把聊天列宽 clamp 到不超过窗口实际可用宽度。
    /// 用 RootGrid.Bounds.Width（真实可视布局宽，不含 OS 边框）+ 侧边栏实际宽，
    /// 而非 Window.Width（含边框，偏大 → chat 会溢出 ~1 字宽被裁）。
    /// chat 列是像素宽，窗口缩小时不会自动收缩，需此 clamp 兜底。</summary>
    private void ClampChatColumn()
    {
        var gridW = RootGrid.Bounds.Width;
        if (gridW <= 0) gridW = Width; // 未布局时兜底
        var sidebarW = RootGrid.ColumnDefinitions[0].ActualWidth;
        if (sidebarW <= 0) sidebarW = (DataContext is MainViewModelBase vm && vm.IsSidebarExpanded) ? 200 : 48;
        var contentMin = RootGrid.ColumnDefinitions[1].MinWidth; // 240
        var maxChat = gridW - sidebarW - contentMin;
        if (maxChat < 300) maxChat = 300; // 极端小窗兜底（chat MinWidth）
        var chat = RootGrid.ColumnDefinitions[2];
        if (chat.Width.IsAbsolute && chat.Width.Value > maxChat)
            chat.Width = new GridLength(maxChat);
    }

    /// <summary>关闭时持久化窗口尺寸 + 聊天栏宽度 + 笔记分隔位置。
    /// 在后台线程写文件（避免 UI 线程 await 死锁），并 Wait 确保关闭前落盘。</summary>
    private void PersistLayout()
    {
        var store = PreferencesStore;
        if (store is null) return;
        double winW = Width, winH = Height, chatW = RootGrid.ColumnDefinitions[2].Width.Value;
        var notes = NotesViewInstance?.CollectLayout();
        try
        {
            Task.Run(async () =>
            {
                await store.SetAsync("ui.winW", winW.ToString("0.#"));
                await store.SetAsync("ui.winH", winH.ToString("0.#"));
                await store.SetAsync("ui.chatW", chatW.ToString("0.#"));
                if (notes is { } n)
                {
                    await store.SetAsync("notes.treeW", n.TreeWidth.ToString("0.#"));
                    await store.SetAsync("notes.previewStar", n.PreviewStar.ToString("0.###"));
                    await store.SetAsync("notes.editorStar", n.EditorStar.ToString("0.###"));
                }
            }).Wait();
        }
        catch { /* 关闭期写入失败忽略 */ }
    }
}
