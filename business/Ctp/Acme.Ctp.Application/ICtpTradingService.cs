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

    Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default);
    CtpTick? GetLatestTick(string instrumentId);
    IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks();
}