namespace Acme.StockTrading.Domain;

/// <summary>
/// 股票经纪网关端口（机制无关 / OS 无关）。
/// 当前实现：FlaUI 操控同花顺下单客户端 GUI（Windows-only，Acme.StockTrading.Infrastructure）。
/// 未来实现：券商原生 API（跨平台）。换适配器 = 换一行 DI 注册，业务层零改动。
/// 端口里没有任何 GUI 概念 —— 那是 FlaUI 适配器内部细节。
/// </summary>
public interface IStockBrokerage
{
    bool IsConnected { get; }
    event EventHandler<string>? LogReceived;

    /// <summary>纯确认：找窗口在不在。在 → 立即成功；不在 → 立即失败。不阻塞、不自动启动。</summary>
    Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>启动客户端 exe（不阻塞、不等待）。启动后手动登录，再 ConnectAsync 确认。</summary>
    Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default);

    Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default);
    Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default);
    Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default);
}
