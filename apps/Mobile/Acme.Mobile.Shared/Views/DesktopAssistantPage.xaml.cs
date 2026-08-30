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
