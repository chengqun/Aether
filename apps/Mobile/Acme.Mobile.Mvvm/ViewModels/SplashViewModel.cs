using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.AppAuth.Application.Auth;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class SplashViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService _authService;
    private readonly ICoreInitializer _coreInitializer;
    private readonly IThemeManager _themeManager;
    private readonly IAppSettings? _appSettings;
    private readonly IPostLoginInitializer? _postLoginInitializer;

    [ObservableProperty]
    private string _statusMessage = "正在初始化...";

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>
    /// 仅当初始化耗时超过阈值(返回用户本地操作慢/首次建库/token 刷新)时才为 true,显示转圈+状态文字。
    /// 返回用户冷启动本地操作快→保持 false,只闪一下 logo 即跳转,体感无过场。
    /// </summary>
    [ObservableProperty]
    private bool _showProgress;

    private volatile bool _initDone;

    public event EventHandler<SplashNavigationEventArgs>? InitializationCompleted;

    public SplashViewModel(IAppAuthService authService,
                          ICoreInitializer coreInitializer,
                          IThemeManager themeManager,
                          IAppSettings? appSettings = null,
                          IPostLoginInitializer? postLoginInitializer = null)
    {
        _authService = authService;
        _coreInitializer = coreInitializer;
        _themeManager = themeManager;
        _appSettings = appSettings;
        _postLoginInitializer = postLoginInitializer;
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        bool isLoggedIn = false;
        _initDone = false;
        ShowProgress = false;

        // 超过阈值仍未完成才补显转圈(返回用户本地操作快→不显示,体感无过场;首次建库/token 刷新慢→显示)
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (!_initDone) ShowProgress = true;
        });

        try
        {
            StatusMessage = "正在初始化主题...";
            _themeManager.Initialize();

            StatusMessage = "正在初始化数据库...";
            await Task.Run(() => _coreInitializer.InitializeAsync());

            StatusMessage = "正在登录...";
            isLoggedIn = await _authService.IsLoggedInAsync();

            // access token 过期时尝试用 refresh token 续期（对齐 AlphaAgent AutoLoginAsync 语义）。
            // IsLoggedInAsync 在 access token 过期（默认 1h TTL）时返 false；若直接跳登录页，refresh token
            // （通常 7-30 天）仍有效却被迫重新输密码。RefreshAsync 无 refresh token 时快速返 false（首次启动无影响）。
            if (!isLoggedIn)
            {
                isLoggedIn = await _authService.RefreshAsync();
            }

            // 自动登录(返回用户)成功:应用本地缓存配置,秒级,直接进 ChatPage
            if (isLoggedIn && _postLoginInitializer != null)
            {
                StatusMessage = "正在加载配置...";
                await Task.Run(() => _postLoginInitializer.InitializeCriticalAsync());
            }

            StatusMessage = "初始化完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"初始化失败: {ex.Message}";
            await Task.Delay(1000);
        }
        finally
        {
            _initDone = true;
            ShowProgress = false;
            IsLoading = false;
            InitializationCompleted?.Invoke(this, new SplashNavigationEventArgs { IsLoggedIn = isLoggedIn });
        }
    }
}
