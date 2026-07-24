using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Application;

/// <summary>
/// CTP 交易应用服务：登录/登出/资金·持仓·报单查询/下单/撤单 + 报单与成交回报事件。
/// 编排 <see cref="ICtpSession"/>（Infrastructure 实现），对 UI 层只暴露领域 DTO，不泄漏 Ctp.Net.Core SDK。
/// </summary>
public interface ICtpTradingService
{
    bool IsConnected { get; }
    string? BrokerId { get; }
    string? UserId { get; }
    CtpLoginResult? LoginResult { get; }

    /// <summary>报单回报（状态更新，按 OrderRef+FrontId+SessionId 匹配同一笔）。</summary>
    event EventHandler<CtpOrder>? OrderReceived;
    /// <summary>成交回报。</summary>
    event EventHandler<CtpTrade>? TradeReceived;
    /// <summary>连接状态变化（Disconnected/Connected/Reconnecting/Closed）。</summary>
    event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default);
    Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default);
}
