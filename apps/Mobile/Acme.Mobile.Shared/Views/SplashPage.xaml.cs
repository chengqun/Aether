using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class SplashPage : ContentPage
{
    private readonly SplashViewModel _vm;

    public SplashPage(SplashViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        Loaded += OnLoaded;
        Disappearing += OnDisappearing;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _vm.InitializationCompleted += OnInitializationCompleted;
        await _vm.InitializeCommand.ExecuteAsync(null);
    }

    private void OnDisappearing(object? sender, EventArgs e)
        => _vm.InitializationCompleted -= OnInitializationCompleted;

    private void OnInitializationCompleted(object? sender, SplashNavigationEventArgs ev)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            // 必须 await：GoToAsync 返回的 Task 若不 await，在 UI 线程 fire-and-forget 会被运行时丢弃，导航不生效（实测卡在 Splash）。
            if (ev.IsLoggedIn)
                await Shell.Current.GoToAsync("//ContactsPage");
            else
                await Shell.Current.GoToAsync("//LoginPage");
        });
}
