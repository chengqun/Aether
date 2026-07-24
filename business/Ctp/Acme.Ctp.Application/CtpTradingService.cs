using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Application;

/// <summary>
/// <see cref="ICtpTradingService"/> 实现：薄编排层，把 UI 层对 CTP 交易的调用转发给
/// <see cref="ICtpSession"/>（Infrastructure 实现），并把会话的报单/成交/连接事件原样转发。
/// 连接状态检查与用户反馈留在 UI 层（ViewModel），本服务只做依赖方向隔离 + 事件桥接。
/// </summary>
public class CtpTradingService : ICtpTradingService
{
    private readonly ICtpSession _session;

    public CtpTradingService(ICtpSession session)
    {
        _session = session;
        _session.OrderReceived += (_, e) => OrderReceived?.Invoke(this, e);
        _session.TradeReceived += (_, e) => TradeReceived?.Invoke(this, e);
        _session.ConnectionStateChanged += (_, e) => ConnectionStateChanged?.Invoke(this, e);
    }

    public bool IsConnected => _session.IsConnected;
    public string? BrokerId => _session.BrokerId;
    public string? UserId => _session.UserId;
    public CtpLoginResult? LoginResult => _session.LoginResult;

    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

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
}
