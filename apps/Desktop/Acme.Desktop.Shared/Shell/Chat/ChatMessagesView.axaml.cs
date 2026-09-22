using Acme.Desktop.Mvvm;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 聊天消息流（共享控件）：滚动消息列表 + 全部消息模板（用户气泡 / agent markdown / 思考 /
/// 工具步骤 / 计划 / 审批卡）+ 贴底跟随 / 历史增量加载 / 工具卡展开 / 复制 等交互。
/// 由 <see cref="ChatView"/>（右栏紧凑，聊天唯一视图面）嵌入。绑 <c>IChatViewHost</c>（DataContext 继承自父视图同款 VM）。
/// </summary>
public partial class ChatMessagesView : UserControl
{
    /// <summary>判定「贴底」的垂直容差（逻辑像素）。距底部小于此值视为用户在底部，流式时自动跟随。</summary>
    private const double StickToBottomTolerance = 24;

    /// <summary>距顶部小于此值（逻辑像素）时触发加载更早的历史轮。</summary>
    private const double HistoryLoadThreshold = 120;

    private IChatViewHost? _vm;
    private ChatTurnItem? _trackedTurn;
    private ChatMessageItem? _trackedChild;
    /// <summary>宿主窗口——订阅其 Activated，在模态弹窗（审批/ask_user）关闭后重新贴底。</summary>
    private Window? _hostWindow;

    /// <summary>是否跟随到底部。流式追加 Content 不触发 CollectionChanged，需靠此标志 + 属性变化驱动滚动。</summary>
    private bool _stickToBottom = true;

    /// <summary>合并同帧多次滚动请求，避免每个 token 都触发一次 ScrollToEnd。</summary>
    private bool _scrollPending;

    /// <summary>正在增量加载更早历史（抑制此期间的自动滚到底 + 滚动事件处理）。</summary>
    private bool _loadingHistory;

    public ChatMessagesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 展开消息区时补一次滚到底。面板收起时 IsVisible=false → ScrollViewer 不布局，流式追加的 token
    /// 触发的 ScrollToEnd 是空操作；展开时滚动位置停在旧处，正在流式输出的气泡落在视窗下方看不到。
    /// 故展开可见时，若正在流式输出则强制贴底并滚到底。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is bool visible && visible)
            OnBecameVisible();
    }

    /// <summary>展开可见：若应贴底，等布局 + markdown 重新渲染后滚到底（收起期间积累的内容需要时间渲染）。</summary>
    private async void OnBecameVisible()
    {
        if (_vm?.IsAgentRunning ?? false)
            _stickToBottom = true; // 流式中：展开应跟随最新输出
        if (!_stickToBottom) return;
        // 等一帧布局刷新，再等一帧 markdown 渲染完成，双次滚动确保到位
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        RequestScrollToBottom();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        RequestScrollToBottom();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 订阅宿主窗口 Activated：模态弹窗（审批/ask_user）关闭后宿主窗被重新激活，
        // 此时流式恢复但布局/焦点偏移常使滚动偏离底部（"焦点跑飞"）——若 agent 正在运行则强制贴底。
        if (_hostWindow is null && TopLevel.GetTopLevel(this) is Window w)
        {
            _hostWindow = w;
            _hostWindow.Activated += OnHostActivated;
        }
        Dispatcher.UIThread.Post(ScrollToBottom);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
            _hostWindow.Activated -= OnHostActivated;
        _hostWindow = null;
    }

    /// <summary>宿主窗重新激活（模态弹窗关闭 / 从其它应用切回）且 agent 运行中：强制贴底，把视窗拉回最新输出。</summary>
    private void OnHostActivated(object? sender, EventArgs e)
    {
        if (_vm?.IsAgentRunning ?? false)
            ReanchorToBottom();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is IChatViewModel oldChatVm)
            oldChatVm.Messages.CollectionChanged -= OnMessagesChanged;
        UntrackAll();
        _vm = DataContext as IChatViewHost;
        if (_vm is IChatViewModel newChatVm)
            newChatVm.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 切换会话（Reset）时重置贴底，并重新跟踪最后一轮（流式追加只改其 Content，不再触发 CollectionChanged）。
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _stickToBottom = true;
        TrackLastTurn();
        if (_loadingHistory) return; // 历史增量加载（Insert(0)）：不触发自动滚到底
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
            RequestScrollToBottom();
    }

    /// <summary>订阅最后一轮——其 Children 增减与末子项 Content/IsStreaming 变化驱动滚动。</summary>
    private void TrackLastTurn()
    {
        UntrackAll();
        if (_vm is IChatViewModel chatVm && chatVm.Messages.Count > 0)
        {
            _trackedTurn = chatVm.Messages[^1];
            _trackedTurn.PropertyChanged += OnTrackedTurnChanged;
            _trackedTurn.Children.CollectionChanged += OnTrackedChildrenChanged;
            TrackLastChild();
        }
    }

    private void TrackLastChild()
    {
        if (_trackedChild != null)
            _trackedChild.PropertyChanged -= OnTrackedChildChanged;
        _trackedChild = _trackedTurn is not null && _trackedTurn.Children.Count > 0
            ? _trackedTurn.Children[^1] : null;
        if (_trackedChild != null)
            _trackedChild.PropertyChanged += OnTrackedChildChanged;
    }

    private void UntrackAll()
    {
        if (_trackedChild != null)
            _trackedChild.PropertyChanged -= OnTrackedChildChanged;
        _trackedChild = null;
        if (_trackedTurn != null)
        {
            _trackedTurn.PropertyChanged -= OnTrackedTurnChanged;
            _trackedTurn.Children.CollectionChanged -= OnTrackedChildrenChanged;
        }
        _trackedTurn = null;
    }

    private void OnTrackedChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TrackLastChild();
        RequestScrollToBottom();
    }

    private void OnTrackedChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Content 变化 = 流式 token 追加（或展开/折叠改变高度）。IsStreaming 结束也会重算高度，一并跟随。
        if (e.PropertyName != nameof(ChatMessageItem.Content)
            && e.PropertyName != nameof(ChatMessageItem.IsStreaming))
            return;
        RequestScrollToBottom();
    }

    private void OnTrackedTurnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatTurnItem.Status))
            RequestScrollToBottom();
    }

    /// <summary>强制贴底并延迟滚到底（等布局/Markdown 渲染稳定）。模态弹框关闭后恢复流式时调用——
    /// 弹窗关闭常使滚动偏离底部（"焦点跑飞"），这里在布局稳定后重新贴底，且每帧重申 _stickToBottom，
    /// 避免中途的布局滚动把它翻假导致后续 token 驱动的滚动失效。</summary>
    private async void ReanchorToBottom()
    {
        // 双帧延迟：第一帧等布局刷新，第二帧等 Markdown 重新渲染完成，各自重申贴底 + 滚一次确保到位
        // （参考 OnBecameVisible 的同款手法）。
        _stickToBottom = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _stickToBottom = true;
        ScrollToBottom();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _stickToBottom = true;
        ScrollToBottom();
    }

    /// <summary>用户滚动时维护贴底标志。OffsetDelta==0 表示仅内容尺寸变化（非用户滚动），不改变标志。</summary>
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (_loadingHistory) return; // 增量加载 / offset 补偿期间忽略
        if (e.OffsetDelta.Y == 0) return; // 仅 Extent/Viewport 变化，不视为用户主动滚动
        var distFromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        _stickToBottom = distFromBottom < StickToBottomTolerance;

        // 滚到底部浮动按钮显隐
        ScrollToBottomButton.IsVisible = !_stickToBottom && sv.Extent.Height > sv.Viewport.Height;

        // 滚到顶部附近且还有更早历史：增量加载，避免切换长会话时一次性渲染全部消息卡顿。
        if (_vm?.HasMoreHistory == true && sv.Extent.Height > sv.Viewport.Height
            && sv.Offset.Y < HistoryLoadThreshold)
        {
            TryLoadHistory(sv);
        }
    }

    /// <summary>滚到顶部时增量加载更早的历史轮，并补偿滚动偏移使视窗停留原位（不被新插入内容顶走）。</summary>
    private async void TryLoadHistory(ScrollViewer sv)
    {
        if (_vm is null || _loadingHistory) return;
        double beforeExtent = sv.Extent.Height;
        double beforeOffset = sv.Offset.Y;
        _loadingHistory = true;
        try
        {
            int loaded = _vm.LoadMoreHistory();
            if (loaded > 0)
            {
                // 等待插入项布局生效后读取 extent 增量，补偿 offset 使原视窗内容停留原位。
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                double delta = sv.Extent.Height - beforeExtent;
                if (delta > 0)
                    sv.Offset = new Vector(0, beforeOffset + delta);
            }
        }
        finally { _loadingHistory = false; }
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

    /// <summary>复制 agent 回答内容到剪贴板。</summary>
    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ChatMessageItem item && !string.IsNullOrEmpty(item.Content))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await ClipboardExtensions.SetTextAsync(clipboard, item.Content);
        }
    }

    /// <summary>点击浮动按钮滚到底部。</summary>
    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollToBottomButton.IsVisible = false;
        ScrollToBottom();
    }
}
