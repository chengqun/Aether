using System.Collections.Specialized;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class AgentChatDetailPage : ContentPage, IQueryAttributable
{
    private AgentChatDetailViewModel? _vm;

    public AgentChatDetailPage(AgentChatDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.StreamingContentUpdated += OnStreamingContentUpdated;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _vm?.SetParameters(query);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentChatDetailViewModel.AgentName))
            Title = _vm?.AgentName ?? "AI 助手";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm != null)
        {
            // Stage 1：强制文字输入模式（语音识别为 stub）
            _vm.IsVoiceMode = false;
            Title = _vm.AgentName;
            await _vm.OnActivatedAsync();
        }
        await ScrollToBottomAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_vm != null) await _vm.OnDeactivatedAsync();
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

    private async Task ScrollToBottomAsync()
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
}
