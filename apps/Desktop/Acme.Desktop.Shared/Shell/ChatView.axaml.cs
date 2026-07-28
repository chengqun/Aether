using Acme.Shell;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

public partial class ChatView : UserControl
{
    /// <summary>判定「贴底」的垂直容差（逻辑像素）。距底部小于此值视为用户在底部，流式时自动跟随。</summary>
    private const double StickToBottomTolerance = 24;

    private MainViewModelBase? _vm;
    private ChatMessageItem? _trackedMsg;

    /// <summary>是否跟随到底部。流式追加 Content 不触发 CollectionChanged，需靠此标志 + 属性变化驱动滚动。</summary>
    private bool _stickToBottom = true;

    /// <summary>合并同帧多次滚动请求，避免每个 token 都触发一次 ScrollToEnd。</summary>
    private bool _scrollPending;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(ScrollToBottom);
    }

    public void ScrollToBottomOnShow()
    {
        Dispatcher.UIThread.Post(ScrollToBottom);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            var chatVm = _vm as IChatViewModel;
            if (chatVm != null)
            {
                chatVm.Messages.CollectionChanged -= OnMessagesChanged;
            }
        }
        UntrackMessage();
        _vm = DataContext as MainViewModelBase;
        if (_vm != null)
        {
            var chatVm = _vm as IChatViewModel;
            if (chatVm != null)
            {
                chatVm.Messages.CollectionChanged += OnMessagesChanged;
            }
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 切换会话（Reset）时重置贴底，并重新跟踪最后一条消息（流式追加只改其 Content，不再触发 CollectionChanged）。
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _stickToBottom = true;
        TrackLastMessage();
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
            RequestScrollToBottom();
    }

    /// <summary>订阅最后一条消息的 PropertyChanged——流式 token 追加 Content 时由此驱动滚动。</summary>
    private void TrackLastMessage()
    {
        UntrackMessage();
        if (_vm is IChatViewModel chatVm && chatVm.Messages.Count > 0)
        {
            _trackedMsg = chatVm.Messages[^1];
            _trackedMsg.PropertyChanged += OnTrackedMessageChanged;
        }
    }

    private void UntrackMessage()
    {
        if (_trackedMsg != null)
        {
            _trackedMsg.PropertyChanged -= OnTrackedMessageChanged;
            _trackedMsg = null;
        }
    }

    private void OnTrackedMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Content 变化 = 流式 token 追加（或展开/折叠改变高度）。IsStreaming 结束也会重算高度，一并跟随。
        if (e.PropertyName != nameof(ChatMessageItem.Content)
            && e.PropertyName != nameof(ChatMessageItem.IsStreaming))
            return;
        RequestScrollToBottom();
    }

    /// <summary>用户滚动时维护贴底标志。OffsetDelta==0 表示仅内容尺寸变化（非用户滚动），不改变标志。</summary>
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (e.OffsetDelta.Y == 0) return; // 仅 Extent/Viewport 变化，不视为用户主动滚动
        var distFromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        _stickToBottom = distFromBottom < StickToBottomTolerance;
    }

    private void RequestScrollToBottom()
    {
        if (!_stickToBottom) return; // 用户向上翻看历史，不强制拉回
        if (_scrollPending) return;  // 同帧已排队
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottom() => MessagesScrollViewer?.ScrollToEnd();

    private void OnToolCardTapped(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Control control && control.DataContext is ChatMessageItem item)
            item.IsExpanded = !item.IsExpanded;
    }

    private void OnStreamingCardTapped(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Control control && control.DataContext is ChatMessageItem item)
            item.IsStreamingExpanded = !item.IsStreamingExpanded;
    }

    private void OnStreamingScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv) sv.ScrollToEnd();
    }

    private void OnDeviceConfigClick(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var settingsDialog = _vm.SettingsDialog;
        if (settingsDialog == null) return;
        _ = settingsDialog.ShowSettingsAsync();
    }
}
