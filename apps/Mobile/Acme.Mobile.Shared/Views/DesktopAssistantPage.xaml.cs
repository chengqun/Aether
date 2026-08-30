using System.Collections.Specialized;
using System.ComponentModel;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 桌面助手页：手机远程控制桌面 agent 的独立功能入口。
/// 顶部「选择电脑」下拉 + 消息列表 + 输入栏。绑定独立 <see cref="DesktopAssistantViewModel"/>
/// （不复用社交聊天 <see cref="ChatDetailViewModel"/>）。
/// </summary>
public partial class DesktopAssistantPage : ContentPage
{
    private readonly DesktopAssistantViewModel _vm;

    public DesktopAssistantPage(DesktopAssistantViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopAssistantViewModel.ConversationName))
            Title = _vm.ConversationName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = _vm.ConversationName;
        await _vm.OnActivatedAsync();
        await ScrollToBottomAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnDeactivatedAsync();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ThrottledScrollToBottom();
    }

    /// <summary>
    /// 滚到底部——**关键：两段式（渲染追赶）**。
    /// 桌面端回复是非流式一次性到达的完整 markdown（≤300 字概览），Indiko MarkdownView 异步解析+排版
    /// （30ms 第一遍滚时 markdown 未必渲染完→拿到的 item 高度是占位值→按过期高度滚动→markdown 渲染后
    /// item 长高但滚动没更新→内容被裁「被挡住」）。
    /// 对照：手机 AI agent 对话页（AgentChatSurface）订阅 StreamingContentUpdated——流式逐 token 追加时
    /// 每次更新都重滚（OnStreamingContentUpdated→ThrottledScrollToBottom），总能追上最终高度，故不裁切。
    /// 桌面页无流式事件，故用固定延时补一次第二滚（等 markdown 渲染完再按真实高度对齐），镜像 agent 页
    /// 「内容更新后重滚」的行为。这正是两页「被挡住」差异的根因。
    /// </summary>
    private void ThrottledScrollToBottom()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(30);
                ScrollLastToEnd();
                // 第二遍：等 Indiko MarkdownView 异步渲染完成后按真实高度再滚一次。
                await Task.Delay(450);
                ScrollLastToEnd();
            }
            catch { }
        });
    }

    private void ScrollLastToEnd()
    {
        if (_vm.Messages.Count > 0)
        {
            var last = _vm.Messages[^1];
            MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
        }
    }

    private async Task ScrollToBottomAsync()
    {
        try
        {
            // 同两段式：OnAppearing 载入历史时也要等 markdown 渲染完再对齐，否则历史最后一条（桌面回复）
            // 按占位高度滚动→渲染后被裁（「历史记录里面看会被挡住」即此路径）。
            await Task.Delay(50);
            ScrollLastToEnd();
            await Task.Delay(450);
            ScrollLastToEnd();
        }
        catch { }
    }
}
