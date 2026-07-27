using Acme.Ctp.Application;
using Acme.Ctp.Domain;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// CTP 期货连接器 ViewModel（Stockapp 租户）。连接/断开 CTP、持久化凭据、显示连接状态。
/// 实现 <see cref="IConnectorViewModel"/> 供 ExpertsViewModel 可选持有。UI 无关。
/// 镜像 AlphaAgent 的 Stockapp ConnectorViewModel。
/// </summary>
public sealed partial class CtpConnectorViewModel : ObservableObject, IConnectorViewModel
{
    private readonly ICtpTradingService _trading;
    private readonly ICtpConfigStore _configStore;

    public CtpConnectorViewModel(ICtpTradingService trading, ICtpConfigStore configStore)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _trading.ConnectionStateChanged += OnConnectionStateChanged;
        _isConnected = _trading.IsConnected;
        _statusText = _isConnected ? "已连接" : "未连接";
    }

    [ObservableProperty] private string _frontAddress = string.Empty;
    [ObservableProperty] private string _mdFrontAddress = string.Empty;
    [ObservableProperty] private string _brokerId = string.Empty;
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _authCode = string.Empty;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private bool _isConnecting;

    /// <summary>从存储加载已存配置到表单，并刷新连接状态。</summary>
    public async Task RefreshAsync()
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
        }
        IsConnected = _trading.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";
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
