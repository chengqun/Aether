using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;
using QuantConnect;

namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// <see cref="ILiveTradingRuntime"/> 实现：注入 <see cref="ICtpSession"/> 到 <see cref="CtpLeanGateway"/>，
/// 后台线程跑 <see cref="LeanLiveRunner"/>（Lean 实时引擎阻塞 CPU 活）。引擎经 CtpBrokerage 桥接同一 session。
/// 前置：CTP 已登录（ICtpSession.IsConnected）；否则 StartAsync 直接返回错误，不启动引擎。
/// </summary>
public sealed class LeanLiveTradingService : ILiveTradingRuntime
{
    private readonly ICtpSession _session;
    private readonly ILogger<LeanLiveTradingService>? _logger;
    private readonly LeanLiveRunner _runner;
    private LiveTradingRuntimeStatus _status = LiveTradingRuntimeStatus.Idle;
    private Task? _runTask;

    public LeanLiveTradingService(ICtpSession session, ILogger<LeanLiveTradingService>? logger = null)
    {
        _session = session;
        _logger = logger;
        _runner = new LeanLiveRunner(logger);
    }

    public LiveTradingRuntimeStatus Status => _status;
    public bool IsRunning => _status == LiveTradingRuntimeStatus.Running;
    public event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> StartAsync(LiveTradingRuntimeRequest request, CancellationToken ct = default)
    {
        if (_status == LiveTradingRuntimeStatus.Running || _status == LiveTradingRuntimeStatus.Starting)
            return Task.FromResult((false, (string?)"实盘引擎已在运行"));

        if (!_session.IsConnected)
            return Task.FromResult((false, (string?)"CTP 未登录——请先登录后再启动实盘引擎"));
        var mdConnect = LeanLiveRunner.MapConnect(request.MarketDataConnect);
        if (mdConnect == null)
            return Task.FromResult((false, (string?)"未提供行情前置连接参数（MdFrontAddress/BrokerId/UserId/Password）"));
        if (request.Instruments.Count == 0)
            return Task.FromResult((false, (string?)"未指定订阅合约"));

        // 注入网关：CtpLeanGateway（brokerage 用：session/md/instruments/日志/P2 交易选项）+ TradingGateway（共享算法用：日志/停止）
        CtpLeanGateway.Reset();
        CtpLeanGateway.Session = _session;
        CtpLeanGateway.MarketDataConnect = mdConnect;
        CtpLeanGateway.Instruments = request.Instruments;
        CtpLeanGateway.LogSink = msg => LogReceived?.Invoke(this, msg);
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions
        {
            EnableRealOrders = request.EnableRealOrders,
            MaxOrderVolume = Math.Max(1, request.MaxOrderVolume),
        };
        TradingGateway.Reset();
        TradingGateway.VerboseLog = request.VerboseLog;
        TradingGateway.LogSink = msg => LogReceived?.Invoke(this, msg);

        SetStatus(LiveTradingRuntimeStatus.Starting);
        EmitLog("实盘引擎启动中…");

        _runTask = Task.Run(() =>
        {
            SetStatus(LiveTradingRuntimeStatus.Running);
            EmitLog("实盘引擎运行中（消费 CTP 行情）");
            try
            {
                var (status, error) = _runner.Run(request);
                EmitLog($"实盘引擎退出：{status}{(error != null ? " / " + error : "")}");
                SetStatus(status == AlgorithmStatus.Completed || status == AlgorithmStatus.Stopped
                    ? LiveTradingRuntimeStatus.Stopped
                    : LiveTradingRuntimeStatus.Error);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "实盘引擎异常");
                EmitLog($"实盘引擎异常: {ex.Message}");
                SetStatus(LiveTradingRuntimeStatus.Error);
            }
            finally
            {
                CtpLeanGateway.Reset();
                TradingGateway.Reset();
            }
        }, ct);

        return Task.FromResult((true, (string?)null));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_status != LiveTradingRuntimeStatus.Running && _status != LiveTradingRuntimeStatus.Starting) return;
        SetStatus(LiveTradingRuntimeStatus.Stopping);
        TradingGateway.StopRequested = true;   // 共享算法 OnData 检测后 Quit
        EmitLog("停止请求已发出，等待引擎退出…");
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(30), ct); EmitLog("引擎已正常退出，实盘已停止"); }
            catch (TimeoutException) { EmitLog("引擎 30s 内未退出，可能仍在处理"); }
            catch (OperationCanceledException) { }
        }
        if (_status == LiveTradingRuntimeStatus.Stopping) SetStatus(LiveTradingRuntimeStatus.Stopped);
    }

    private void SetStatus(LiveTradingRuntimeStatus s)
    {
        _status = s;
        StatusChanged?.Invoke(this, s);
    }

    private void EmitLog(string msg) => LogReceived?.Invoke(this, msg);
}
