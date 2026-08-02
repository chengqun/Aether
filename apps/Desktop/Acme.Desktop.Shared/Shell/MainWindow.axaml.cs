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

    /// <summary>点击历史会话行后收起下拉（Command 仍照常触发加载会话）。</summary>
    public void OnConversationClicked(object? sender, RoutedEventArgs e)
        => HistoryFlyoutButton.Flyout?.Hide();

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
                && cw is >= 300 and <= 800)
                RootGrid.ColumnDefinitions[2].Width = new GridLength(cw);
        }
        catch { /* 读取失败用默认宽度 */ }
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
