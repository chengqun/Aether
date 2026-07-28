using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Application;

public interface ICtpTradingService
{
    bool IsConnected { get; }
    string? BrokerId { get; }
    string? UserId { get; }
    CtpLoginResult? LoginResult { get; }

    event EventHandler<CtpOrder>? OrderReceived;
    event EventHandler<CtpTrade>? TradeReceived;
    event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;
    event EventHandler<CtpTick>? TickReceived;

    bool IsMdSubscribed { get; }

    Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default);

    /// <summary>订阅行情（行情前置独立于交易登录，显式传入连接参数 + 凭据）。</summary>
    Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default);
    /// <summary>退订行情。</summary>
    void UnsubscribeMarketData();
    CtpTick? GetLatestTick(string instrumentId);
    IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks();
}