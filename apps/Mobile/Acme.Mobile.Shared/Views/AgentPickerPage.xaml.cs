using System.Linq;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class AgentPickerPage : ContentPage
{
    private readonly AgentPickerViewModel _vm;

    public AgentPickerPage(AgentPickerViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigateToAgentChatRequested += OnNavigateToAgentChat;
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not AgentPickItem item) return;
        // 清除选择，便于再次进入时仍可点同一项
        ((CollectionView)sender!).SelectedItem = null;
        await _vm.PickAgentAsync(item.Name);
    }

    private async void OnNavigateToAgentChat(object? sender, string agentName)
    {
        await Shell.Current.GoToAsync($"AgentChatDetailPage?agentName={Uri.EscapeDataString(agentName)}");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
    }
}
