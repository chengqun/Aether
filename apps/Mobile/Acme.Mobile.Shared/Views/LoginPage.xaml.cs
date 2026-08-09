using Acme.AppAuth.Application.Auth;
using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _vm;
    private readonly IAppAuthService? _authService;
    private readonly IAppSettings? _appSettings;

    public LoginPage(LoginViewModel viewModel, IAppAuthService? authService = null, IAppSettings? appSettings = null)
    {
        InitializeComponent();
        _vm = viewModel;
        _authService = authService;
        _appSettings = appSettings;
        BindingContext = _vm;

        // 预填已保存的服务器地址
        if (_appSettings != null && !string.IsNullOrEmpty(_appSettings.ServerBaseAddress))
            ServerEntry.Text = _appSettings.ServerBaseAddress;

        _vm.LoginCompleted += OnLoginCompleted;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        // 登录前把用户填的服务器地址应用到 auth 服务 + 持久化（供下次启动 bootstrap 读）
        var url = ServerEntry.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(url) && _authService != null)
        {
            _authService.ConfigureBaseUrl(url);
            _appSettings?.SetString("app.serverUrl", url);
        }

        if (_vm.LoginCommand.CanExecute(null))
            await _vm.LoginCommand.ExecuteAsync(null);
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Shell.Current.GoToAsync("//ContactsPage");
        });
}
