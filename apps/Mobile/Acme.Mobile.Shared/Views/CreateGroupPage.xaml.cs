using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class CreateGroupPage : ContentPage
{
    private readonly CreateGroupViewModel _vm;

    public CreateGroupPage(CreateGroupViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
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
        _vm.NavigationRequested -= OnNavigationRequested;
        await _vm.OnDeactivatedAsync();
    }
}
