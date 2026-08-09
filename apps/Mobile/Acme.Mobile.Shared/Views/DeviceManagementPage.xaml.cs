using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class DeviceManagementPage : ContentPage
{
    private readonly DeviceManagementViewModel _vm;

    public DeviceManagementPage(DeviceManagementViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
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
