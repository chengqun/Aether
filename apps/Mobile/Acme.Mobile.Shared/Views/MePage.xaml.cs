using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class MePage : ContentPage
{
    private readonly MeViewModel _vm;
    private bool _isBindingTheme; // guards against Toggled firing from OneWay binding updates during load

    public MePage(MeViewModel viewModel)
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

    private async void OnThemeToggled(object? sender, ToggledEventArgs e)
    {
        if (_isBindingTheme)
            return;
        await _vm.ToggleThemeAsync(e.Value);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isBindingTheme = true;
        try
        {
            await _vm.OnActivatedAsync();
        }
        finally
        {
            _isBindingTheme = false;
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.NavigationRequested -= OnNavigationRequested;
        await _vm.OnDeactivatedAsync();
    }
}
