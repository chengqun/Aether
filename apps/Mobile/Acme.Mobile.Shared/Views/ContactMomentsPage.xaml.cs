using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ContactMomentsPage : ContentPage, IQueryAttributable
{
    private readonly ContactMomentsViewModel _vm;

    public ContactMomentsPage(ContactMomentsViewModel viewModel)
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
