using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Application;

public class CtpTradingService : ICtpTradingService
{
    private readonly ICtpSession _session;

    public CtpTradingService(ICtpSession session)
    {
        _session = session;
        _session.OrderReceived += (_, e) => OrderReceived?.Invoke(this, e);
        _session.TradeReceived += (_, e) => TradeReceived?.Invoke(this, e);
        _session.ConnectionStateChanged += (_, e) => ConnectionStateChanged?.Invoke(this, e);
        _session.TickReceived += (_, e) => TickReceived?.Invoke(this, e);
    }

    public bool IsConnected => _session.IsConnected;
    public bool IsMdSubscribed => _session.IsMdSubscribed;
    public string? BrokerId => _session.BrokerId;
    public string? UserId => _session.UserId;
    public CtpLoginResult? LoginResult => _session.LoginResult;

    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;
    public event EventHandler<CtpTick>? TickReceived;

    public Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default)
        => _session.LoginAsync(request, ct);

    public Task LogoutAsync(CancellationToken ct = default)
    {
        _session.Logout();
        return Task.CompletedTask;
    }

    public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
        => _session.QueryAccountAsync(ct);

    public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
        => _session.QueryPositionAsync(instrumentId, ct);

    public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default)
        => _session.QueryOrderAsync(instrumentId, ct);

    public Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default)
        => _session.OrderInsertAsync(request, ct);

    public Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default)
        => _session.OrderActionAsync(orderRef, ct);

    public Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default)
        => _session.SubscribeMarketDataAsync(connect, symbols, ct);

    public void UnsubscribeMarketData()
        => _session.UnsubscribeMarketData();

    public CtpTick? GetLatestTick(string instrumentId)
        => _session.GetLatestTick(instrumentId);

    public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks()
        => _session.GetAllLatestTicks();
}