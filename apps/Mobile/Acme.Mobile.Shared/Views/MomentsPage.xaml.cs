using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class MomentsPage : ContentPage
{
    private readonly MomentsViewModel _vm;

    public MomentsPage(MomentsViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.PublishMomentRequested += OnPublishMomentRequested;
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnPublishMomentRequested(object? sender, PublishMomentRequestedEvent e)
    {
        var content = await DisplayPromptAsync("发布朋友圈", "分享点什么...", "发布", "取消", maxLength: 500);
        if (!string.IsNullOrWhiteSpace(content))
            await _vm.PublishMomentAsync(content);
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
