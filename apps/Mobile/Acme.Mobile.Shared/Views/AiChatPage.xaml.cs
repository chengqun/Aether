using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class AiChatPage : ContentPage
{
    private readonly AiChatViewModel _vm;

    public AiChatPage(AiChatViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        SubscribeNavigationEvents();
    }

    /// <summary>订阅导航事件（幂等：-= then +=）。</summary>
    private void SubscribeNavigationEvents()
    {
        _vm.NavigateToAgentChatDetailRequested -= OnNavigateToAgentChatDetail;
        _vm.NavigateToAgentPickerRequested -= OnNavigateToAgentPicker;
        _vm.NavigateToAgentChatDetailRequested += OnNavigateToAgentChatDetail;
        _vm.NavigateToAgentPickerRequested += OnNavigateToAgentPicker;
    }

    private void UnsubscribeNavigationEvents()
    {
        _vm.NavigateToAgentChatDetailRequested -= OnNavigateToAgentChatDetail;
        _vm.NavigateToAgentPickerRequested -= OnNavigateToAgentPicker;
    }

    private async void OnNavigateToAgentChatDetail(object? sender, AgentChatDetailNavigationEventArgs e)
    {
        await Shell.Current.GoToAsync(
            $"AgentChatDetailPage?sessionId={e.SessionId}" +
            $"&stockId={e.StockId ?? ""}" +
            $"&stockName={Uri.EscapeDataString(e.StockName ?? "")}" +
            $"&agentName={Uri.EscapeDataString(e.AgentName)}");
    }

    private async void OnNavigateToAgentPicker(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("AgentPickerPage");
    }

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
