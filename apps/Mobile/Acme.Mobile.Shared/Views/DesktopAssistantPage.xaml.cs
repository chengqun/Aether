using System.Collections.Specialized;
using System.ComponentModel;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 桌面助手页（专用薄壳）：顶部「选择电脑」下拉 + 复用 <see cref="ChatDetailViewModel"/> 的设备会话链路。
/// 「一个 AI，两个运行位置」里的「桌面」位——多台 desktop 设备各自独立会话，下拉切换。
/// 消息渲染/心跳过滤/离线补拉/审批全部复用 ChatDetailViewModel，本页只增设备选择 UI。
/// </summary>
public partial class DesktopAssistantPage : ContentPage, IQueryAttributable
{
    private readonly ChatDetailViewModel _vm;

    public DesktopAssistantPage(ChatDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>
    /// 接收导航 query：AI 页 💻 入口传入初始设备（pendingContactId + pendingContactType=设备）。
    /// 置桌面助手模式标志，让 VM 加载 desktop 设备列表。
    /// </summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _vm.IsDesktopAssistant = true;

        if (query.TryGetValue("pendingContactId", out var cid) && cid != null)
            _vm.PendingContactId = cid.ToString()!;

        if (query.TryGetValue("pendingContactType", out var ct) && ct != null)
            _vm.PendingContactType = ct.ToString()!;
        else
            _vm.PendingContactType = "设备";

        if (query.TryGetValue("conversationName", out var convName) && convName != null)
            _vm.ConversationName = convName.ToString()!;
        else
            _vm.ConversationName = "桌面助手";
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatDetailViewModel.ConversationName))
            Title = _vm.ConversationName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _vm.IsVoiceMode = false; // 桌面助手无语音输入（轻控制）
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

    private void ThrottledScrollToBottom()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(30);
                if (_vm.Messages.Count > 0)
                {
                    var last = _vm.Messages[^1];
                    MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
                }
            }
            catch { }
        });
    }

    private async Task ScrollToBottomAsync()
    {
        try
        {
            await Task.Delay(50);
            if (_vm.Messages.Count > 0)
            {
                var last = _vm.Messages[^1];
                MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
            }
        }
        catch { }
    }
}
