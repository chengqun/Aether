using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.Application;

/// <summary>
/// 实盘应用层输入（面向 UI/工具）。镜像 <see cref="LiveTradingRuntimeRequest"/> 的纯字段，
/// 经 <see cref="ILiveTradingApplicationService"/> 映射成 Runtime 请求。不含 Lean/CTP SDK 类型。
/// </summary>
public sealed class LiveTradingRunInput
{
    public IReadOnlyList<string> Instruments { get; set; } = Array.Empty<string>();
    public string StrategyName { get; set; } = "SmaCross";
    public decimal InitialCash { get; set; } = 1_000_000m;
    public int FastPeriod { get; set; } = 10;
    public int SlowPeriod { get; set; } = 30;
    public BarResolution Resolution { get; set; } = BarResolution.Minute;
    public MarketDataConnectInput? MarketDataConnect { get; set; }
}

/// <summary>
/// 实盘应用编排服务契约：薄封装 Runtime 的 <see cref="ILiveTradingRuntime"/>，
/// 代理状态/日志事件给 UI。前置：CTP 已登录（ICtpSession 已连接）。
/// </summary>
public interface ILiveTradingApplicationService
{
    LiveTradingRuntimeStatus Status { get; }
    bool IsRunning { get; }
    event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    event EventHandler<string>? LogReceived;

    Task<(bool Success, string? Error)> StartAsync(LiveTradingRunInput input, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
