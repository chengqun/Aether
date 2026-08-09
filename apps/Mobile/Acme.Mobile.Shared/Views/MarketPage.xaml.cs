using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class MarketPage : ContentPage
{
    private readonly MarketViewModel _vm;

    public MarketPage(MarketViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
    }
}
