using System.Collections.ObjectModel;
using System.Text;
using Acme.Desktop.Mvvm;
using Acme.Trading.Application;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Scripting;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// 回测 + 实盘 ViewModel（Stockapp 租户）。只依赖 Trading.Application + Runtime.Domain 枚举，
/// 不依赖 Trading.Runtime.Lean —— 运行时无关（Lean now，未来换运行时只换 DI 注册）。
/// 回测走 IBacktestApplicationService；实盘走 ILiveTradingApplicationService（P2：默认仅信号日志，
/// 「真实下单」开关打开后经 CTP 真实报单——启动前二次确认）。
/// </summary>
public partial class TradingViewModel : ObservableObject
{
    private readonly IBacktestApplicationService _backtest;
    private readonly ILiveTradingApplicationService _live;
    private readonly IStrategyScriptingService _scripting;
    private readonly ILeanAlgorithmRuntime _leanBacktest;
    private readonly ILeanAlgorithmScriptingService _leanScripting;
    private readonly IPreferencesStore _preferences;
    public CtpConnectorViewModel Connector { get; }

    public TradingViewModel(IBacktestApplicationService backtest, ILiveTradingApplicationService live, CtpConnectorViewModel connector, IStrategyScriptingService scripting, ILeanAlgorithmRuntime leanBacktest, ILeanAlgorithmScriptingService leanScripting, IPreferencesStore preferences)
    {
        _backtest = backtest;
        _live = live;
        Connector = connector;
        _scripting = scripting;
        _leanBacktest = leanBacktest;
        _leanScripting = leanScripting;
        _preferences = preferences;

        _live.StatusChanged += OnLiveStatusChanged;
        _live.LogReceived += OnLiveLogReceived;
        // 策略脚本加载完成（启动 + labs 文件变更重加载后）→ 刷新策略选择器 + 重新加载参数
        _scripting.StrategiesLoaded += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(StrategyOptions));
            LoadStrategyParameters(IsLiveMode ? LiveStrategy : StrategyName);
        });
        // Lean 算法脚本加载完成 → 刷新 Lean 算法选择器 + 重新加载参数（独立链路，与 IStrategy 路径互不影响）
        _leanScripting.Loaded += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(LeanAlgorithmOptions));
            RefreshLeanLoadErrors();
            LoadLeanAlgorithmParameters(LeanAlgorithmName);
        });

        // 恢复上次选中的策略（JsonPreferencesStore.GetAsync 同步完成，GetAwaiter().GetResult() 不死锁）
        _strategyName = _preferences.GetAsync("trading.backtestStrategy").GetAwaiter().GetResult() ?? "";
        _liveStrategy = _preferences.GetAsync("trading.liveStrategy").GetAwaiter().GetResult() ?? "";
        _leanAlgorithmName = _preferences.GetAsync("trading.leanAlgorithm").GetAwaiter().GetResult() ?? "";
        var savedEngine = _preferences.GetAsync("trading.engine").GetAwaiter().GetResult();
        _engine = string.Equals(savedEngine, "LeanAlgorithm", StringComparison.OrdinalIgnoreCase)
            ? StrategyEngine.LeanAlgorithm : StrategyEngine.IStrategy;
        // P2 真实下单开关 + 手数保险丝（跨重启持久化；开关默认关，绝不自动打开）
        _liveRealOrders = ParseBool(_preferences.GetAsync("trading.liveRealOrders").GetAwaiter().GetResult());
        _liveMaxOrderVolume = _preferences.GetAsync("trading.liveMaxVolume").GetAwaiter().GetResult() ?? "1";
        // 启动时加载当前策略的参数（构造函数设字段不触发 OnStrategyNameChanged，需显式调）
        LoadStrategyParameters(IsLiveMode ? _liveStrategy : _strategyName);
        LoadLeanAlgorithmParameters(_leanAlgorithmName);
    }

    partial void OnStrategyNameChanged(string value)
    {
        _preferences.SetAsync("trading.backtestStrategy", value).SafeFireAndForget();
        if (!IsLiveMode) LoadStrategyParameters(value);
    }

    partial void OnLiveStrategyChanged(string value)
    {
        _preferences.SetAsync("trading.liveStrategy", value).SafeFireAndForget();
        if (IsLiveMode) LoadStrategyParameters(value);
    }

    partial void OnIsLiveModeChanged(bool value)
    {
        LoadStrategyParameters(value ? LiveStrategy : StrategyName);
        OnPropertyChanged(nameof(IsEngineSelectorVisible));
        OnPropertyChanged(nameof(IsLeanAlgorithmSectionVisible));
        OnPropertyChanged(nameof(IsIStrategySectionVisible));
    }

    /// <summary>从 StrategyRegistry 加载策略参数定义 → 填充 StrategyParameterRows。</summary>
    private void LoadStrategyParameters(string? strategyName)
    {
        StrategyParameterRows.Clear();
        if (string.IsNullOrWhiteSpace(strategyName)) { CurrentStrategyDescription = ""; return; }
        CurrentStrategyDescription = $"当前策略：{strategyName}";
        foreach (var def in StrategyRegistry.GetParameterDefinitions(strategyName))
            StrategyParameterRows.Add(new StrategyParameterRow { Key = def.Key, Label = def.Label, Value = def.DefaultValue });
    }

    /// <summary>从 StrategyParameterRows 构建 Dictionary（传给 RunInput.StrategyParameters）。</summary>
    private Dictionary<string, string> BuildStrategyParameters()
    {
        var dict = new Dictionary<string, string>();
        foreach (var row in StrategyParameterRows)
            if (!string.IsNullOrWhiteSpace(row.Key))
                dict[row.Key] = row.Value ?? "";
        return dict;
    }

    // ═══ 引擎切换（IStrategy 壳 vs Lean 算法直写）═══
    // 三条策略路径：CodeStrategy/Strategy 走 IStrategy 壳（Lean 中立）；LeanAlgorithm 直接写 QCAlgorithm（与 Lean 耦合）。
    // 引擎切换只影响回测路径；实盘当前只支持 IStrategy 壳（Lean 实盘=后续）。

    /// <summary>回测引擎：IStrategy（壳，Lean 中立）或 LeanAlgorithm（直写 QCAlgorithm，复用 Lean 全部能力）。</summary>
    public StrategyEngine[] EngineOptions { get; } = new[] { StrategyEngine.IStrategy, StrategyEngine.LeanAlgorithm };

    [ObservableProperty] private StrategyEngine _engine = StrategyEngine.IStrategy;

    partial void OnEngineChanged(StrategyEngine value)
    {
        _preferences.SetAsync("trading.engine", value.ToString()).SafeFireAndForget();
        OnPropertyChanged(nameof(IsLeanAlgorithmSectionVisible));
        OnPropertyChanged(nameof(IsIStrategySectionVisible));
    }

    /// <summary>Lean 引擎是否可用作实盘（当前否——Lean 实盘为后续）。</summary>
    public bool IsLeanLiveSupported => false;

    /// <summary>策略 tab：显示引擎切换（仅回测；实盘只支持 IStrategy）。</summary>
    public bool IsEngineSelectorVisible => !IsLiveMode;
    /// <summary>策略 tab：显示 Lean 算法选择器（仅回测 + Lean 引擎）。</summary>
    public bool IsLeanAlgorithmSectionVisible => !IsLiveMode && Engine == StrategyEngine.LeanAlgorithm;
    /// <summary>策略 tab：显示 IStrategy 选择器（实盘，或回测 IStrategy 引擎）。</summary>
    public bool IsIStrategySectionVisible => IsLiveMode || Engine == StrategyEngine.IStrategy;

    // ═══ Lean 算法（直写 QCAlgorithm）═══

    public string[] LeanAlgorithmOptions => _leanScripting.GetRegisteredNames().ToArray();

    [ObservableProperty] private string _leanAlgorithmName = "";

    partial void OnLeanAlgorithmNameChanged(string value)
    {
        _preferences.SetAsync("trading.leanAlgorithm", value).SafeFireAndForget();
        LoadLeanAlgorithmParameters(value);
    }

    /// <summary>当前 Lean 算法的参数行（UI 动态渲染）。切算法时从 LeanAlgorithmScriptingService.GetParameterDefinitions 重建。</summary>
    public ObservableCollection<StrategyParameterRow> LeanAlgorithmParameterRows { get; } = new();

    /// <summary>Lean 算法编译错误（名→错误），UI 展示以便排查"下拉为空"等问题。空串=无错误。</summary>
    [ObservableProperty] private string _leanLoadErrors = "";
    public bool HasLeanLoadErrors => !string.IsNullOrEmpty(LeanLoadErrors);
    /// <summary>无算法且无错误 → 显示"新建笔记"引导。</summary>
    public bool ShowLeanEmptyHint => LeanAlgorithmOptions.Length == 0 && !HasLeanLoadErrors;

    private void RefreshLeanLoadErrors()
    {
        var errs = _leanScripting.LoadErrors;
        LeanLoadErrors = errs.Count == 0 ? "" : string.Join("\n", errs.Select(kv => $"• {kv.Key}：{kv.Value}"));
        OnPropertyChanged(nameof(HasLeanLoadErrors));
        OnPropertyChanged(nameof(ShowLeanEmptyHint));
    }

    /// <summary>从 LeanAlgorithmScriptingService 加载算法参数定义 → 填充 LeanAlgorithmParameterRows。</summary>
    private void LoadLeanAlgorithmParameters(string? algoName)
    {
        LeanAlgorithmParameterRows.Clear();
        if (string.IsNullOrWhiteSpace(algoName)) return;
        foreach (var def in _leanScripting.GetParameterDefinitions(algoName))
            LeanAlgorithmParameterRows.Add(new StrategyParameterRow { Key = def.Key, Label = def.Label, Value = def.DefaultValue });
    }

    /// <summary>从 LeanAlgorithmParameterRows 构建 Dictionary（传给 LeanAlgorithmBacktestRequest.StrategyParameters）。</summary>
    private Dictionary<string, string> BuildLeanAlgorithmParameters()
    {
        var dict = new Dictionary<string, string>();
        foreach (var row in LeanAlgorithmParameterRows)
            if (!string.IsNullOrWhiteSpace(row.Key))
                dict[row.Key] = row.Value ?? "";
        return dict;
    }

    // ═══ 回测 ═══

    public string[] StrategyOptions => StrategyRegistry.GetRegisteredNames().ToArray();
    public string[] ResolutionOptions { get; } = new[] { "Daily", "Hour", "Minute" };

    [ObservableProperty] private string _symbol = "ag2610";
    [ObservableProperty] private string _strategyName = "";
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddDays(-365);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private decimal _initialCash = 100_000m;
    [ObservableProperty] private string _resolution = "Daily";

    /// <summary>当前策略的参数行（UI 动态渲染）。切策略时从 StrategyRegistry.GetParameterDefinitions 重建。</summary>
    public ObservableCollection<StrategyParameterRow> StrategyParameterRows { get; } = new();
    /// <summary>当前策略描述（策略 tab 显示）。</summary>
    [ObservableProperty] private string _currentStrategyDescription = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RequestStartLiveCommand))]
    private bool _isRunning;
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

    // 回测/实盘共享 Lean 全局静态，绝不可并发——互查对方运行态（LeanGlobalLock 是硬兜底）。
    private bool CanRun => !IsRunning && !IsLiveRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        StatusMessage = "回测运行中…";
        Error = string.Empty;
        try
        {
            BacktestResult result;
            if (Engine == StrategyEngine.LeanAlgorithm)
            {
                // Lean 算法路径：直写 QCAlgorithm 子类，绕过 IStrategy 壳，复用 Lean 全部能力。
                var request = new LeanAlgorithmBacktestRequest
                {
                    StrategyName = LeanAlgorithmName,
                    Symbol = Symbol,
                    StartDate = (StartDate ?? DateTime.Today.AddDays(-365)).Date,
                    EndDate = (EndDate ?? DateTime.Today).Date,
                    InitialCash = InitialCash,
                    Resolution = ParseResolution(Resolution),
                    StrategyParameters = BuildLeanAlgorithmParameters(),
                };
                result = await Task.Run(() => _leanBacktest.RunAsync(request, CancellationToken.None));
            }
            else
            {
                // IStrategy 路径（既有，零改动）：CodeStrategy / Strategy 经 TradingHostAlgorithm 壳跑。
                var input = new BacktestRunInput
                {
                    Symbol = Symbol,
                    StrategyName = StrategyName,
                    StartDate = (StartDate ?? DateTime.Today.AddDays(-365)).Date,
                    EndDate = (EndDate ?? DateTime.Today).Date,
                    InitialCash = InitialCash,
                    FastPeriod = 10,
                    SlowPeriod = 30,
                    StrategyParameters = BuildStrategyParameters(),
                    Resolution = ParseResolution(Resolution),
                };
                result = await Task.Run(() => _backtest.RunAsync(input, CancellationToken.None));
            }

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

    [RelayCommand]
    private void ClearLiveLog()
    {
        _liveLogBuf.Clear();
        LiveLog = "";
    }

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
    [ObservableProperty] private bool _verboseLog;
    [ObservableProperty] private bool _isLiveMode;   // false=回测模式, true=实盘模式
    [ObservableProperty] private string _liveStrategy = "";
    [ObservableProperty] private string _liveStatusText = "未启动";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RequestStartLiveCommand), nameof(StopLiveCommand))]
    private bool _isLiveRunning;
    [ObservableProperty] private string _liveLog = "";

    // ═══ P2 真实下单（开关 + 手数保险丝 + 启动确认）═══

    /// <summary>真实下单开关：false=仅信号日志（P1 行为）；true=经 CTP 真实报单。持久化，默认关。</summary>
    [ObservableProperty] private bool _liveRealOrders;
    /// <summary>单笔开仓手数上限（平仓不受限）。字符串绑定 TextBox，启动时 parse（非法/小于 1 回退 1）。</summary>
    [ObservableProperty] private string _liveMaxOrderVolume = "1";
    /// <summary>启动确认弹层显隐（镜像期货页 IsConfirmingOrder 两步确认）。</summary>
    [ObservableProperty] private bool _isConfirmingLiveStart;
    /// <summary>启动确认弹层摘要（合约/策略/手数上限）。</summary>
    [ObservableProperty] private string _liveStartSummary = "";

    public bool HasRealOrderWarning => LiveRealOrders;

    partial void OnLiveRealOrdersChanged(bool value)
    {
        _preferences.SetAsync("trading.liveRealOrders", value ? "true" : "false").SafeFireAndForget();
        OnPropertyChanged(nameof(HasRealOrderWarning));
    }

    partial void OnLiveMaxOrderVolumeChanged(string value)
        => _preferences.SetAsync("trading.liveMaxVolume", value).SafeFireAndForget();

    private int ParseMaxVolume() => int.TryParse(LiveMaxOrderVolume, out var v) && v >= 1 ? v : 1;

    private static bool ParseBool(string? s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);

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
            RequestStartLiveCommand.NotifyCanExecuteChanged();
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

    private bool CanStartLive => !IsLiveRunning && !IsRunning;
    private bool CanStopLive => IsLiveRunning;

    /// <summary>启动入口：开关关 → 直接启动（P1 路径不变）；开关开 → 先弹二次确认（真实下单不可逆）。</summary>
    [RelayCommand(CanExecute = nameof(CanStartLive))]
    private async Task RequestStartLiveAsync()
    {
        // 守卫（与启动条件一致，提前拦截避免确认后才失败）
        if (!Connector.IsConnected) { AppendLiveLog("[实盘] CTP 未登录——请先在「期货」页登录后再启动实盘引擎"); return; }
        if (string.IsNullOrEmpty(Connector.MdFrontAddress) || string.IsNullOrEmpty(Connector.Password))
        { AppendLiveLog("[实盘] 行情前置参数缺失（Connector 未配置 md 地址/密码）"); return; }
        var instruments = LiveInstruments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (instruments.Length == 0) { AppendLiveLog("[实盘] 请填写至少一个合约（如 ag2610）"); return; }

        if (!LiveRealOrders)
        {
            await StartLiveCoreAsync();
            return;
        }
        LiveStartSummary = $"合约 {string.Join(",", instruments)} ｜ 策略 {LiveStrategy} ｜ 单笔开仓上限 {ParseMaxVolume()} 手\n" +
                           "真实下单模式：策略信号将直接向 CTP 报单（对手价限价），请确认风险。";
        IsConfirmingLiveStart = true;
    }

    [RelayCommand]
    private async Task ConfirmStartLiveAsync()
    {
        IsConfirmingLiveStart = false;
        await StartLiveCoreAsync();
    }

    [RelayCommand]
    private void CancelStartLive() => IsConfirmingLiveStart = false;

    private async Task StartLiveCoreAsync()
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

        AppendLiveLog($"[实盘] 启动：合约={string.Join(",", instruments)} 策略={LiveStrategy} 分辨率={LiveResolution} " +
                      $"真实下单={(LiveRealOrders ? $"开（单笔开仓上限 {ParseMaxVolume()} 手）" : "关（仅信号）")}");
        var input = new LiveTradingRunInput
        {
            Instruments = instruments,
            StrategyName = LiveStrategy,
            InitialCash = 1_000_000m,
            FastPeriod = 10,
            SlowPeriod = 30,
            StrategyParameters = BuildStrategyParameters(),
            Resolution = ParseResolution(LiveResolution),
            VerboseLog = VerboseLog,
            EnableRealOrders = LiveRealOrders,
            MaxOrderVolume = ParseMaxVolume(),
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

/// <summary>策略参数行（UI 动态渲染：Label + TextBox）。</summary>
public partial class StrategyParameterRow : ObservableObject
{
    public string Key { get; set; } = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";
}

/// <summary>回测引擎：IStrategy（壳，Lean 中立）/ LeanAlgorithm（直写 QCAlgorithm，与 Lean 耦合）。</summary>
public enum StrategyEngine
{
    IStrategy,
    LeanAlgorithm,
}
