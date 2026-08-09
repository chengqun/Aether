using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ServiceAccountPostDetailPage : ContentPage, IQueryAttributable
{
    private readonly ServiceAccountPostDetailViewModel _vm;

    public ServiceAccountPostDetailPage(ServiceAccountPostDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _vm.SetParameters(query);
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
