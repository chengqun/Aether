using Acme.StockTrading.Domain;

namespace Acme.StockTrading.Application;

/// <summary>UI/AI 经此契约访问股票经纪，不直接依赖 Runtime 端口。</summary>
public interface IStockTradingApplicationService
{
    bool IsConnected { get; }
    event EventHandler<string>? LogReceived;

    Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default);
    Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default);
    Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default);
    Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default);
}
