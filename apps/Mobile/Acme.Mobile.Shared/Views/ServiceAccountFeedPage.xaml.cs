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
        // 不取消订阅 NavigateToPostDetailRequested：Shell 缓存 page+VM 同生命周期，
        // 构造器只跑一次——若 OnDisappearing 取消订阅而 OnAppearing 不重新订阅，
        // 返回后事件无人监听 → 链接只能点一次（与 ContactsPage/MePage 等多数页面一致的安全模式）。
        await _vm.OnDeactivatedAsync();
    }
}
