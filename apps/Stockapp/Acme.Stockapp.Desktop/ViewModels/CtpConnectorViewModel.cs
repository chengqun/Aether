using Acme.Ctp.Application;
using Acme.Ctp.Domain;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// CTP 期货连接器卡片 ViewModel（Stockapp 租户）。连接/断开 CTP、持久化凭据、显示连接状态。
/// 继承 <see cref="ConnectorCardViewModel"/> 作为连接器 tab 的一张卡片；「+」打开 CTP 设置对话框
/// （<see cref="CtpLoginWindow"/>，对话框内填表单 + 连接）。UI 无关。
/// </summary>
public sealed partial class CtpConnectorViewModel : ConnectorCardViewModel
{
    private readonly ICtpTradingService _trading;
    private readonly ICtpConfigStore _configStore;

    public CtpConnectorViewModel(ICtpTradingService trading, ICtpConfigStore configStore,
        IConnectorSettingsDialogService dialogs)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));

        Icon = "📈";
        IconBackground = "#E8F5E9";
        Title = "CTP 期货交易";
        Description = "连接期货柜台，供交易/行情工具调用";
        PrimaryCommand = new AsyncRelayCommand(() => dialogs.ShowSettingsAsync(this));

        _trading.ConnectionStateChanged += OnConnectionStateChanged;
        IsConnected = _trading.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";
    }

    [ObservableProperty] private string _frontAddress = string.Empty;
    [ObservableProperty] private string _mdFrontAddress = string.Empty;
    [ObservableProperty] private string _brokerId = string.Empty;
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _authCode = string.Empty;
    [ObservableProperty] private bool _autoLogin;

    [ObservableProperty] private bool _isConnecting;

    /// <summary>从存储加载已存配置到表单，刷新连接状态，若启用自动登录则自动连接。</summary>
    public override async Task RefreshAsync()
    {
        var cfg = await _configStore.GetAsync();
        if (cfg is not null)
        {
            FrontAddress = cfg.FrontAddress;
            MdFrontAddress = cfg.MdFrontAddress;
            BrokerId = cfg.BrokerId;
            UserId = cfg.UserId;
            Password = cfg.Password;
            AppId = cfg.AppId;
            AuthCode = cfg.AuthCode;
            AutoLogin = cfg.AutoLogin;
        }
        IsConnected = _trading.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        // 自动登录：有配置 + 未连接 + AutoLogin 开启
        if (AutoLogin && !IsConnected && !string.IsNullOrWhiteSpace(FrontAddress) && !string.IsNullOrWhiteSpace(UserId))
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) return;
        if (string.IsNullOrWhiteSpace(FrontAddress) || string.IsNullOrWhiteSpace(UserId))
        {
            StatusText = "请填写交易前置地址与用户号";
            return;
        }

        IsConnecting = true;
        StatusText = "连接中…";
        try
        {
            // 持久化凭据
            await _configStore.SaveAsync(new CtpConnectionConfig
            {
                FrontAddress = FrontAddress,
                MdFrontAddress = MdFrontAddress,
                BrokerId = BrokerId,
                UserId = UserId,
                Password = Password,
                AppId = AppId,
                AuthCode = AuthCode,
                AutoLogin = AutoLogin,
            });

            var result = await _trading.LoginAsync(new CtpLoginRequest
            {
                FrontAddress = FrontAddress,
                MdFrontAddress = MdFrontAddress,
                BrokerId = BrokerId,
                UserId = UserId,
                Password = Password,
                AppId = AppId,
                AuthCode = AuthCode,
            });
            StatusText = $"已连接（交易日 {result.TradingDay}）";
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsConnecting = false;
            IsConnected = _trading.IsConnected;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _trading.LogoutAsync();
            StatusText = "已断开";
        }
        catch (Exception ex)
        {
            StatusText = $"断开失败：{ex.Message}";
        }
        finally
        {
            IsConnected = _trading.IsConnected;
        }
    }

    private void OnConnectionStateChanged(object? sender, CtpConnectionStatus status)
    {
        IsConnected = status == CtpConnectionStatus.Connected;
        StatusText = status switch
        {
            CtpConnectionStatus.Connected => "已连接",
            CtpConnectionStatus.Reconnecting => "重连中…",
            CtpConnectionStatus.Closed => "闭市",
            _ => "未连接",
        };
    }
}
