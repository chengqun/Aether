using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ContactDetailPage : ContentPage, IQueryAttributable
{
    private readonly ContactDetailViewModel _vm;

    public ContactDetailPage(ContactDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.PopAndRefreshRequested += OnPopAndRefreshRequested;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _vm.SetParameters(query);
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnPopAndRefreshRequested(object? sender, EventArgs e)
    {
        await Shell.Current.Navigation.PopAsync();
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
