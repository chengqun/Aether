using Acme.StockTrading.Domain;
using Microsoft.Extensions.Logging;

namespace Acme.StockTrading.Application;

/// <summary>薄封装 IStockBrokerage 端口，代理日志事件。</summary>
public sealed class StockTradingApplicationService : IStockTradingApplicationService
{
    private readonly IStockBrokerage _brokerage;
    private readonly ILogger<StockTradingApplicationService>? _logger;

    public StockTradingApplicationService(IStockBrokerage brokerage, ILogger<StockTradingApplicationService>? logger = null)
    {
        _brokerage = brokerage;
        _logger = logger;
        _brokerage.LogReceived += (s, msg) =>
        {
            _logger?.LogInformation("StockBrokerage: {Message}", msg);
            LogReceived?.Invoke(s, msg);
        };
    }

    public bool IsConnected => _brokerage.IsConnected;
    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default) => _brokerage.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => _brokerage.DisconnectAsync(ct);
    public Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default) => _brokerage.LaunchClientAsync(ct);
    public Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default) => _brokerage.PlaceOrderAsync(request, ct);
    public Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default) => _brokerage.CancelOrderAsync(orderNo, ct);
    public Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default) => _brokerage.QueryPositionsAsync(ct);
}
