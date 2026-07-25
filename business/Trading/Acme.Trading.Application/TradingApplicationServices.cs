using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.Application;

/// <summary>
/// <see cref="IBacktestApplicationService"/> 实现：薄编排层，把 UI 的 <see cref="BacktestRunInput"/>
/// 映射成 Runtime 的 <see cref="BacktestRuntimeRequest"/>（Lean-中性），转发给 <see cref="IBacktestRuntime"/>。
/// Lean 参数键的拼装在 Runtime.Lean runner 内，本层不感知 Lean。
/// </summary>
public sealed class BacktestApplicationService : IBacktestApplicationService
{
    private readonly IBacktestRuntime _runtime;

    public BacktestApplicationService(IBacktestRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <inheritdoc />
    public Task<BacktestResult> RunAsync(BacktestRunInput input, CancellationToken ct = default)
    {
        var request = new BacktestRuntimeRequest
        {
            StrategyName = input.StrategyName,
            Symbol = input.Symbol,
            StartDate = input.StartDate,
            EndDate = input.EndDate,
            InitialCash = input.InitialCash,
            FastPeriod = input.FastPeriod,
            SlowPeriod = input.SlowPeriod,
            Resolution = input.Resolution,
        };
        return _runtime.RunAsync(request, ct);
    }
}

/// <summary>
/// <see cref="ILiveTradingApplicationService"/> 实现：薄编排层，映射 <see cref="LiveTradingRunInput"/>
/// 成 <see cref="LiveTradingRuntimeRequest"/> 转发给 <see cref="ILiveTradingRuntime"/>，并代理状态/日志事件。
/// </summary>
public sealed class LiveTradingApplicationService : ILiveTradingApplicationService
{
    private readonly ILiveTradingRuntime _runtime;

    public LiveTradingApplicationService(ILiveTradingRuntime runtime)
    {
        _runtime = runtime;
        _runtime.StatusChanged += (s, st) => StatusChanged?.Invoke(s, st);
        _runtime.LogReceived += (s, msg) => LogReceived?.Invoke(s, msg);
    }

    public LiveTradingRuntimeStatus Status => _runtime.Status;
    public bool IsRunning => _runtime.IsRunning;
    public event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> StartAsync(LiveTradingRunInput input, CancellationToken ct = default)
    {
        var request = new LiveTradingRuntimeRequest
        {
            Instruments = input.Instruments,
            StrategyName = input.StrategyName,
            InitialCash = input.InitialCash,
            FastPeriod = input.FastPeriod,
            SlowPeriod = input.SlowPeriod,
            Resolution = input.Resolution,
            MarketDataConnect = input.MarketDataConnect,
        };
        return _runtime.StartAsync(request, ct);
    }

    public Task StopAsync(CancellationToken ct = default) => _runtime.StopAsync(ct);
}
