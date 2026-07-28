using System.Text;
using Acme.Shell;
using Acme.Trading.Application;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// 回测 + 实盘 ViewModel（Stockapp 租户）。只依赖 Trading.Application + Runtime.Domain 枚举，
/// 不依赖 Trading.Runtime.Lean —— 运行时无关（Lean now，未来换运行时只换 DI 注册）。
/// 回测走 IBacktestApplicationService；实盘走 ILiveTradingApplicationService（P1：行情/信号链路通，不下单）。
/// 镜像 AlphaAgent 的 BacktestViewModel + CtpViewModel 实盘段。
/// </summary>
public partial class TradingViewModel : ObservableObject
{
    private readonly IBacktestApplicationService _backtest;
    private readonly ILiveTradingApplicationService _live;
    public CtpConnectorViewModel Connector { get; }

    public TradingViewModel(IBacktestApplicationService backtest, ILiveTradingApplicationService live, CtpConnectorViewModel connector)
    {
        _backtest = backtest;
        _live = live;
        Connector = connector;

        _live.StatusChanged += OnLiveStatusChanged;
        _live.LogReceived += OnLiveLogReceived;
    }

    // ═══ 回测 ═══

    public string[] StrategyOptions => StrategyRegistry.GetRegisteredNames().ToArray();
    public string[] ResolutionOptions { get; } = new[] { "Daily", "Hour", "Minute" };

    [ObservableProperty] private string _symbol = "ag2610";
    [ObservableProperty] private string _strategyName = "SmaCross";
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddDays(-365);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private decimal _initialCash = 100_000m;
    [ObservableProperty] private int _fastPeriod = 10;
    [ObservableProperty] private int _slowPeriod = 30;
    [ObservableProperty] private string _resolution = "Daily";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private BacktestStatistics? _statistics;
    [ObservableProperty] private int _equityPointCount;
    [ObservableProperty] private IList<Point> _equityPoints = new List<Point>();
    [ObservableProperty] private string _logText = string.Empty;

    public bool HasResult => Statistics != null && string.IsNullOrEmpty(Error);
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnStatisticsChanged(BacktestStatistics? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasError));
    }
    partial void OnErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasError));
    }

    private bool CanRun => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        StatusMessage = "回测运行中…";
        Error = string.Empty;
        try
        {
            var input = new BacktestRunInput
            {
                Symbol = Symbol,
                StrategyName = StrategyName,
                StartDate = (StartDate ?? DateTime.Today.AddDays(-365)).Date,
                EndDate = (EndDate ?? DateTime.Today).Date,
                InitialCash = InitialCash,
                FastPeriod = FastPeriod,
                SlowPeriod = SlowPeriod,
                Resolution = ParseResolution(Resolution),
            };
            var result = await Task.Run(() => _backtest.RunAsync(input, CancellationToken.None));

            if (result.IsSuccess)
            {
                Statistics = result.Statistics;
                EquityPointCount = result.EquityCurve.Count;
                EquityPoints = BuildEquityPoints(result.EquityCurve);
                StatusMessage = $"回测完成（{result.AlgorithmStatus}）";
            }
            else
            {
                Error = result.Error ?? "回测失败（未知原因）";
                StatusMessage = "回测失败";
            }
            LogText = string.Join("\n", result.Logs);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusMessage = "回测异常";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;

    /// <summary>把权益曲线归一化到 1000×300 画布坐标（Polyline 用）。永返回非 null（空曲线返回空列表）。</summary>
    private static IList<Point> BuildEquityPoints(IReadOnlyList<EquityPoint> curve)
    {
        if (curve is null || curve.Count == 0) return new List<Point>();
        var min = curve.Min(p => p.Value);
        var max = curve.Max(p => p.Value);
        var range = max != min ? (double)(max - min) : 1.0;
        var n = curve.Count;
        return curve.Select((p, i) => new Point(
            n == 1 ? 0 : (double)i / (n - 1) * 1000.0,
            300.0 - (double)(p.Value - min) / range * 280.0 - 10.0)).ToList();
    }

    // ═══ 实盘 ═══

    public string[] LiveResolutionOptions { get; } = new[] { "Minute", "Hour", "Daily", "Tick" };

    [ObservableProperty] private string _liveInstruments = "ag2610";
    [ObservableProperty] private string _liveResolution = "Minute";
    [ObservableProperty] private string _liveStrategy = "SmaCross";
    [ObservableProperty] private string _liveStatusText = "未启动";
    [ObservableProperty] private bool _isLiveRunning;
    [ObservableProperty] private string _liveLog = "";

    private readonly StringBuilder _liveLogBuf = new();

    private void OnLiveStatusChanged(object? sender, LiveTradingRuntimeStatus status)
        => Dispatcher.UIThread.Post(() =>
        {
            LiveStatusText = status switch
            {
                LiveTradingRuntimeStatus.Idle => "未启动",
                LiveTradingRuntimeStatus.Starting => "启动中…",
                LiveTradingRuntimeStatus.Running => "运行中",
                LiveTradingRuntimeStatus.Stopping => "停止中…",
                LiveTradingRuntimeStatus.Stopped => "已停止",
                LiveTradingRuntimeStatus.Error => "异常退出",
                _ => status.ToString(),
            };
            IsLiveRunning = status == LiveTradingRuntimeStatus.Running;
            StartLiveCommand.NotifyCanExecuteChanged();
            StopLiveCommand.NotifyCanExecuteChanged();
        });

    private void OnLiveLogReceived(object? sender, string line)
        => Dispatcher.UIThread.Post(() => AppendLiveLog(line));

    private void AppendLiveLog(string line)
    {
        _liveLogBuf.AppendLine(line);
        if (_liveLogBuf.Length > 40_000)
            _liveLogBuf.Remove(0, 20_000);
        LiveLog = _liveLogBuf.ToString();
    }

    private bool CanStartLive => !IsLiveRunning;
    private bool CanStopLive => IsLiveRunning;

    [RelayCommand(CanExecute = nameof(CanStartLive))]
    private async Task StartLiveAsync()
    {
        if (!Connector.IsConnected)
        {
            AppendLiveLog("[实盘] CTP 未登录——请先在「期货」页登录后再启动实盘引擎");
            return;
        }
        if (string.IsNullOrEmpty(Connector.MdFrontAddress) || string.IsNullOrEmpty(Connector.Password))
        {
            AppendLiveLog("[实盘] 行情前置参数缺失（Connector 未配置 md 地址/密码）");
            return;
        }
        var instruments = LiveInstruments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (instruments.Length == 0) { AppendLiveLog("[实盘] 请填写至少一个合约（如 ag2610）"); return; }

        AppendLiveLog($"[实盘] 启动：合约={string.Join(",", instruments)} 策略={LiveStrategy} 分辨率={LiveResolution}");
        var input = new LiveTradingRunInput
        {
            Instruments = instruments,
            StrategyName = LiveStrategy,
            InitialCash = 1_000_000m,
            FastPeriod = 10,
            SlowPeriod = 30,
            Resolution = ParseResolution(LiveResolution),
            MarketDataConnect = new MarketDataConnectInput
            {
                MdFrontAddress = Connector.MdFrontAddress,
                BrokerId = Connector.BrokerId,
                UserId = Connector.UserId,
                Password = Connector.Password,
            },
        };
        var (ok, err) = await _live.StartAsync(input);
        if (!ok) AppendLiveLog($"[实盘] 启动失败：{err}");
    }

    [RelayCommand(CanExecute = nameof(CanStopLive))]
    private async Task StopLiveAsync()
    {
        AppendLiveLog("[实盘] 请求停止…");
        await _live.StopAsync();
    }

    private static BarResolution ParseResolution(string s) => s switch
    {
        "Daily" => BarResolution.Daily,
        "Hour" => BarResolution.Hour,
        "Minute" => BarResolution.Minute,
        "Second" => BarResolution.Second,
        "Tick" => BarResolution.Tick,
        _ => BarResolution.Daily,
    };
}
