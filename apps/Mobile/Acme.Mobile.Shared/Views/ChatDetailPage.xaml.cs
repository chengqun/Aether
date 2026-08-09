using System.Collections.Specialized;
using System.ComponentModel;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ChatDetailPage : ContentPage, IQueryAttributable
{
    private readonly ChatDetailViewModel _vm;

    public ChatDetailPage(ChatDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>
    /// Receives navigation query params from two sources:
    ///  - ChatPage: conversationId + name
    ///  - ContactDetailPage: pendingContactId + pendingContactType + conversationName + conversationType
    /// </summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("conversationId", out var convId) && convId != null)
            _vm.ConversationId = convId.ToString()!;

        if (query.TryGetValue("name", out var name) && name != null)
            _vm.ConversationName = name.ToString()!;
        else if (query.TryGetValue("conversationName", out var convName) && convName != null)
            _vm.ConversationName = convName.ToString()!;

        if (query.TryGetValue("pendingContactId", out var cid) && cid != null)
            _vm.PendingContactId = cid.ToString()!;
        else if (query.TryGetValue("contactId", out cid) && cid != null)
            _vm.PendingContactId = cid.ToString()!;

        if (query.TryGetValue("pendingContactType", out var ct) && ct != null)
            _vm.PendingContactType = ct.ToString()!;
        else if (query.TryGetValue("contactType", out ct) && ct != null)
            _vm.PendingContactType = ct.ToString()!;

        if (query.TryGetValue("conversationType", out var convType) && convType != null
            && int.TryParse(convType.ToString(), out var cti))
            _vm.ConversationType = cti;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatDetailViewModel.ConversationName))
            Title = _vm.ConversationName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Stage 1: force text input mode (voice recognition is a stub)
        _vm.IsVoiceMode = false;
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
