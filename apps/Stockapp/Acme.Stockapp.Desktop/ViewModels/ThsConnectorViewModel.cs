using Acme.Desktop.Mvvm;
using Acme.StockTrading.Application;
using Acme.StockTrading.Domain;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// 同花顺下单客户端连接器 ViewModel（连接器 tab 的一个子面板，UI 无关）。
/// 经 <see cref="IStockTradingApplicationService"/> 连接/启动，经 <see cref="IStockTradingOptions"/>
/// 读写 exe 路径/窗口标题（改 options 即时生效， FlaUI 适配器每次操作现读）。
/// 配置持久化到 <see cref="IPreferencesStore"/>：
///   - stocktrade.exePath / windowTitle：连接参数；
///   - stocktrade.configured：用户曾成功连接过（= 配置已验证就绪）。
/// 启动期若 configured=true，自动尝试 ConnectAsync 重新确认窗口是否还在（同花顺客户端需人工登录，
/// 故不自动启动 exe，只确认窗口）——窗口在则「已连接」，不在则提示「请启动客户端」（配置仍有效）。
/// 与 <see cref="CtpConnectorViewModel"/> 并列，作为连接器 tab 的一张卡片
/// （由复合 <see cref="StockappConnectorViewModel"/> 统一对外）。
/// </summary>
public partial class ThsConnectorViewModel : ConnectorCardViewModel
{
    private const string KeyExePath = "stocktrade.exePath";
    private const string KeyWindowTitle = "stocktrade.windowTitle";
    private const string KeyConfigured = "stocktrade.configured";

    private readonly IStockTradingApplicationService _service;
    private readonly IStockTradingOptions _options;
    private readonly IPreferencesStore _preferences;
    private readonly Dispatcher _dispatcher;

    /// <summary>用户曾成功连接过（配置已验证）。持久化，重启后据此尝试自动重连确认窗口。</summary>
    private bool _configured;

    public ThsConnectorViewModel(
        IStockTradingApplicationService service,
        IStockTradingOptions options,
        IPreferencesStore? preferences,
        IConnectorSettingsDialogService dialogs)
    {
        _service = service;
        _options = options;
        _preferences = preferences!;
        _dispatcher = Dispatcher.UIThread;

        Icon = "💹";
        IconBackground = "#E3F2FD";
        Title = "同花顺股票交易";
        Description = "连接同花顺下单客户端，供股票下单/撤单/持仓工具调用";
        PrimaryCommand = new AsyncRelayCommand(() => dialogs.ShowSettingsAsync(this));

        _service.LogReceived += OnLogReceived;

        // 启动期用已持久化的配置回填 options（FlaUI 适配器读 options 即时生效）
        _clientExecutablePath = preferences?.GetAsync(KeyExePath).GetAwaiter().GetResult() ?? "";
        _clientWindowTitle = preferences?.GetAsync(KeyWindowTitle).GetAwaiter().GetResult() ?? options.ClientWindowTitle;
        var cfg = preferences?.GetAsync(KeyConfigured).GetAwaiter().GetResult();
        _configured = bool.TryParse(cfg, out var b) && b;
        _options.ClientExecutablePath = _clientExecutablePath;
        _options.ClientWindowTitle = _clientWindowTitle;
    }

    [ObservableProperty] private string _clientExecutablePath = "";
    [ObservableProperty] private string _clientWindowTitle = "网上股票交易系统5.0";

    [ObservableProperty] private bool _isBusy;

    /// <summary>忙碌时禁用「+」主操作。</summary>
    partial void OnIsBusyChanged(bool value) => IsPrimaryEnabled = !value;

    /// <summary>启动期/切到连接器 tab 时刷新：若曾配置过且当前未连接，自动尝试重新确认窗口。</summary>
    public override async Task RefreshAsync()
    {
        if (_configured && !_service.IsConnected)
        {
            await ConnectCoreAsync(autoConfirm: true);
        }
        else
        {
            IsConnected = _service.IsConnected;
            StatusText = IsConnected ? "已连接" : "未连接";
        }
    }

    partial void OnClientExecutablePathChanged(string value)
    {
        _options.ClientExecutablePath = value;
        _ = _preferences.SetAsync(KeyExePath, string.IsNullOrEmpty(value) ? null : value);
    }

    partial void OnClientWindowTitleChanged(string value)
    {
        _options.ClientWindowTitle = value;
        _ = _preferences.SetAsync(KeyWindowTitle, string.IsNullOrEmpty(value) ? null : value);
    }

    private void OnLogReceived(object? sender, string msg)
    {
        // FlaUI/STA 后台线程事件 → 切 UI 线程刷新状态
        _dispatcher.Post(() =>
        {
            IsConnected = _service.IsConnected;
            if (IsConnected && (StatusText == "未连接" || StatusText.Contains("失败")))
                StatusText = "已连接";
        });
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientExecutablePath))
        {
            StatusText = "请先在连接设置里填 xiadan.exe 路径";
            return;
        }
        IsBusy = true;
        try
        {
            var (ok, err) = await _service.LaunchClientAsync();
            StatusText = ok ? "客户端已启动，请手动登录后点连接" : $"启动失败: {err}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task ConnectAsync() => ConnectCoreAsync(autoConfirm: false);

    /// <summary>连接（确认窗口在不在）。
    /// autoConfirm=true：由 RefreshAsync 自动发起的重连确认——失败时不报「连接失败」，
    /// 而是提示「请启动客户端」（配置仍有效，只是客户端没运行）。</summary>
    private async Task ConnectCoreAsync(bool autoConfirm)
    {
        IsBusy = true;
        try
        {
            var (ok, err) = await _service.ConnectAsync();
            IsConnected = _service.IsConnected;
            if (ok)
            {
                StatusText = "已连接";
                if (!_configured)
                {
                    _configured = true;   // 首次成功连接 → 标记配置就绪并持久化
                    _ = _preferences.SetAsync(KeyConfigured, "true");
                }
            }
            else
            {
                StatusText = autoConfirm
                    ? "未连接（请启动同花顺客户端并登录）"
                    : $"连接失败: {err}";
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _service.DisconnectAsync();
            IsConnected = _service.IsConnected;
            StatusText = "已断开";
            // 断开不撤销 configured：配置仍有效，只是主动断开；下次 Refresh 会重新确认窗口。
        }
        finally { IsBusy = false; }
    }
}
