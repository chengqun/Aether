using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class AiSettingsPage : ContentPage
{
    private readonly AiSettingsViewModel _vm;

    public AiSettingsPage(AiSettingsViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnDeactivatedAsync();
    }
}
