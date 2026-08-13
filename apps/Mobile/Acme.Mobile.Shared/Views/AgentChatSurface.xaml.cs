using System.Collections.Specialized;
using System.ComponentModel;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 可复用的 AI 聊天面板（CollectionView + 输入栏 + 5 种消息气泡模板）。
/// 同时被推送页 <see cref="AgentChatDetailPage"/>（Transient VM）和 AI tab（Singleton VM）托管。
/// BindingContext 为 <see cref="AgentChatDetailViewModel"/>；滚动订阅在此自动接线。
/// </summary>
public partial class AgentChatSurface : ContentView
{
    private AgentChatDetailViewModel? _vm;

    public AgentChatSurface()
    {
        InitializeComponent();
        BindingContextChanged += OnBindingContextChanged;
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        // 解绑旧 VM
        if (_vm != null)
        {
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.StreamingContentUpdated -= OnStreamingContentUpdated;
        }

        _vm = BindingContext as AgentChatDetailViewModel;

        if (_vm != null)
        {
            _vm.Messages.CollectionChanged += OnMessagesChanged;
            _vm.StreamingContentUpdated += OnStreamingContentUpdated;
        }
    }

    /// <summary>供宿主页 OnAppearing 调用，滚动到底。</summary>
    public async Task ScrollToBottomAsync()
    {
        try
        {
            await Task.Delay(50);
            if (_vm != null && _vm.Messages.Count > 0)
            {
                var last = _vm.Messages[^1];
                MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
            }
        }
        catch { }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ThrottledScrollToBottom();
    }

    private void OnStreamingContentUpdated() => ThrottledScrollToBottom();

    private void ThrottledScrollToBottom()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(30);
                if (_vm != null && _vm.Messages.Count > 0)
                {
                    var last = _vm.Messages[^1];
                    MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
                }
            }
            catch { }
        });
    }
}
