using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 「设置」二级页（我 → 设置）。退出登录藏于此，点退出前二次确认（防误触）。
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel viewModel)
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

    /// <summary>退出登录前二次确认（防误触）。</summary>
    private async void OnLogoutTapped(object? sender, TappedEventArgs e)
    {
        var ok = await DisplayAlert("退出登录", "确定要退出当前账号吗？", "退出", "取消");
        if (ok && _vm.LogoutCommand.CanExecute(null))
            await _vm.LogoutCommand.ExecuteAsync(null);
    }
}
