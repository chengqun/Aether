using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class AgentContactDetailPage : ContentPage, IQueryAttributable
{
    private AgentContactDetailViewModel? _vm;

    public AgentContactDetailPage(AgentContactDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _vm?.SetParameters(query);

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnLoaded(object sender, EventArgs e)
    {
        if (_vm != null) await _vm.OnActivatedAsync();
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        var confirm = await DisplayAlert("删除 Agent", $"确定删除「{_vm.AgentName}」？", "删除", "取消");
        if (!confirm) return;
        await _vm.DeleteAgentCommand.ExecuteAsync(null);
    }
}
