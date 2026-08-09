using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class ServiceAccountFeedPage : ContentPage
{
    private readonly ServiceAccountFeedViewModel _vm;

    public ServiceAccountFeedPage(ServiceAccountFeedViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigateToPostDetailRequested += OnNavigateToPostDetail;
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
