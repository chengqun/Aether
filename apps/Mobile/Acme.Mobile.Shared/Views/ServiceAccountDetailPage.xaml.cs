using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ServiceAccountDetailPage : ContentPage, IQueryAttributable
{
    private readonly ServiceAccountDetailViewModel _vm;

    public ServiceAccountDetailPage(ServiceAccountDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigateToPostDetailRequested += OnNavigateToPostDetail;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _vm.SetParameters(query);
    }

    private async void OnNavigateToPostDetail(object? sender, ServiceAccountPostNavigationEventArgs e)
    {
        await Shell.Current.GoToAsync($"ServiceAccountPostDetailPage?postId={e.PostId}");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.NavigateToPostDetailRequested -= OnNavigateToPostDetail;
        await _vm.OnDeactivatedAsync();
    }
}
