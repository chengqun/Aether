using Acme.Desktop.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 右栏紧凑聊天视图：头部（连接/设备配置/✕折叠/历史会话/新聊天）+ 底部紧凑输入 + 嵌入
/// <see cref="ChatMessagesView"/>（消息区/模板/滚动 全在共享控件）。与全屏 <see cref="ChatPageView"/>
/// 共享同一 <see cref="IChatViewHost"/> VM，仅头部与输入区布局不同。
/// </summary>
public partial class ChatView : UserControl
{
    /// <summary>聊天面板显隐服务（侧栏「AI 聊天」toggle + 面板内 ✕ 共用）。OnLoaded 解析——仅折叠，展开走侧栏 toggle。</summary>
    private IChatPanelService? _chatPanelService;

    /// <summary>轻量 VM 引用——仅用于 OnDeviceConfigClick 取 SettingsDialog。</summary>
    private IChatViewHost? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => _vm = DataContext as IChatViewHost;

    private void OnLoaded(object? sender, RoutedEventArgs e)
        => _chatPanelService ??= DesktopApp.Services?.GetService<IChatPanelService>();

    /// <summary>面板内 ✕ 折叠按钮：折叠聊天右栏（视图不销毁，流式不中断）。展开走侧栏「AI 聊天」toggle。</summary>
    private void OnCollapseClick(object? sender, RoutedEventArgs e)
        => _chatPanelService?.SetChatVisible(false);

    private void OnDeviceConfigClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SettingsDialog is { } settingsDialog)
            settingsDialog.ShowSettingsAsync().SafeFireAndForget();
    }

    /// <summary>点击历史会话行后收起下拉。
    /// 必须延迟到下一帧再 Hide：Button.OnClick 先 RaiseEvent(Click) 再 Command.Execute，
    /// 若在 Click 里同步 Hide 会把按钮从视觉树摘掉、打断后续命令执行 → 会话加载不出来。
    /// Post 到调度器队列，确保 Command 先执行完再关 Flyout。</summary>
    public void OnConversationClicked(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => HistoryFlyoutButton.Flyout?.Hide());
}
