using Acme.Desktop.Mvvm;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 全屏 AI 聊天页（首屏默认 nav）：精简头部 + <see cref="ChatMessagesView"/> + 居中 ChatGPT 式输入。
/// 与右栏紧凑 <see cref="ChatView"/> 共享同一 <see cref="IChatViewHost"/> VM；头部/输入布局不同。
/// 全屏页无 ✕ 折叠按钮（收起全屏页 = 切到其它 nav；右栏显隐由侧栏 toggle 管）。
/// </summary>
public partial class ChatPageView : UserControl
{
    /// <summary>轻量 VM 引用——仅用于 OnDeviceConfigClick 取 SettingsDialog。</summary>
    private IChatViewHost? _vm;

    public ChatPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => _vm = DataContext as IChatViewHost;

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
