using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ChatPage : ContentPage
{
    private readonly ChatViewModel _vm;

    public ChatPage(ChatViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        SubscribeNavigationEvents();
    }

    /// <summary>
    /// Subscribe the 3 custom navigation events (idempotent — -= then += avoids duplicates
    /// when re-subscribed in <see cref="OnAppearing"/> after <see cref="OnDisappearing"/>).
    /// </summary>
    private void SubscribeNavigationEvents()
    {
        _vm.NavigateToChatDetailRequested -= OnNavigateToChatDetail;
        _vm.NavigateToAgentChatDetailRequested -= OnNavigateToAgentChatDetail;
        _vm.NavigateToServiceAccountFeedRequested -= OnNavigateToServiceAccountFeed;

        _vm.NavigateToChatDetailRequested += OnNavigateToChatDetail;
        _vm.NavigateToAgentChatDetailRequested += OnNavigateToAgentChatDetail;
        _vm.NavigateToServiceAccountFeedRequested += OnNavigateToServiceAccountFeed;
    }

    private void UnsubscribeNavigationEvents()
    {
        _vm.NavigateToChatDetailRequested -= OnNavigateToChatDetail;
        _vm.NavigateToAgentChatDetailRequested -= OnNavigateToAgentChatDetail;
        _vm.NavigateToServiceAccountFeedRequested -= OnNavigateToServiceAccountFeed;
    }

    private async void OnNavigateToChatDetail(object? sender, ChatDetailNavigationEventArgs e)
    {
        await Shell.Current.GoToAsync(
            $"ChatDetailPage?conversationId={e.ConversationId}&name={Uri.EscapeDataString(e.ConversationName)}");
    }

    private async void OnNavigateToAgentChatDetail(object? sender, AgentChatDetailNavigationEventArgs e)
    {
        await Shell.Current.GoToAsync(
            $"AgentChatDetailPage?sessionId={e.SessionId}" +
            $"&stockId={e.StockId ?? ""}" +
            $"&stockName={Uri.EscapeDataString(e.StockName ?? "")}" +
            $"&agentName={Uri.EscapeDataString(e.AgentName)}");
    }

    private async void OnNavigateToServiceAccountFeed(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("ServiceAccountFeedPage");
    }

    /// <summary>
    /// CollectionView SelectionChanged → fire the VM's navigation command.
    /// The binding sets <see cref="ChatViewModel.SelectedConversation"/> before this handler runs.
    /// </summary>
    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm.SelectedConversation is not null)
            await _vm.NavigateToChatDetailAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SubscribeNavigationEvents();
        await _vm.OnActivatedAsync();
    }

    protected override async void OnDisappearing()
    {
        UnsubscribeNavigationEvents();
        await _vm.OnDeactivatedAsync();
        base.OnDisappearing();
    }
}
