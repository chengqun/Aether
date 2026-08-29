using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class WorkflowCreatePage : ContentPage
{
    private readonly WorkflowCreateViewModel _vm;

    public WorkflowCreatePage(WorkflowCreateViewModel viewModel)
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

    private async void OnLoaded(object sender, EventArgs e)
    {
        if (_vm != null) await _vm.OnActivatedAsync();
    }
}
