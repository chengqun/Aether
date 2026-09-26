using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Ctp.Domain;

/// <summary>
/// CTP 会话原语契约（登录/查询/下单/撤单/行情订阅）。
/// Infrastructure.Ctp 实现此接口（CLAUDE.md 规则：Infrastructure 实现的接口放 Domain.Abstractions）。
/// Application/UI 层不依赖此接口，而依赖上层的 <c>ICtpTradingService</c>/<c>ICtpMarketDataService</c>。
/// 方法签名只使用本命名空间下的领域 DTO，不泄漏 Ctp.Net.Core SDK 类型。
/// </summary>
public interface ICtpSession
{
    bool IsConnected { get; }
    bool IsMdSubscribed { get; }
    CtpLoginResult? LoginResult { get; }
    string? BrokerId { get; }
    string? UserId { get; }

    /// <summary>报单回报（含状态更新，按 OrderRef+FrontId+SessionId 匹配同一笔）。</summary>
    event EventHandler<CtpOrder>? OrderReceived;
    /// <summary>成交回报。</summary>
    event EventHandler<CtpTrade>? TradeReceived;
    /// <summary>Tick 行情推送。</summary>
    event EventHandler<CtpTick>? TickReceived;
    /// <summary>连接状态变化（Disconnected/Connected/Reconnecting/Closed）。盘中断线走 Reconnecting，闭市走 Closed。</summary>
    event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    /// <summary>登录（交易前置 + 行情前置 + 凭据；行情前置/密码会话内缓存供后续订阅复用）。</summary>
    Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default);

    /// <summary>登出并释放交易/行情连接。</summary>
    void Logout();

    /// <summary>查询资金账户（复用登录凭据）。</summary>
    Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default);

    /// <summary>查询持仓（可选按合约过滤）。</summary>
    Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default);

    /// <summary>查询报单（可选按合约过滤）。</summary>
    Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default);

    /// <summary>
    /// 查询合约表。仿真柜台（前置 sim）返回 host 上架的合约（「行情随柜台」的行情板数据源）；
    /// 真实链路暂无合约查询（<c>CtpSessionService</c> 返回空表——行情板走证券库主力合约）。
    /// </summary>
    Task<IReadOnlyList<CtpInstrument>> QueryInstrumentsAsync(CancellationToken ct = default);

    /// <summary>下单。</summary>
    Task<CtpOrderInsertResult> OrderInsertAsync(CtpOrderRequest request, CancellationToken ct = default);

    /// <summary>撤单。</summary>
    Task OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct = default);

    /// <summary>订阅行情（行情前置独立于交易登录，显式传入连接参数 + 凭据）。</summary>
    Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default);

    /// <summary>退订行情。</summary>
    void UnsubscribeMarketData();

    /// <summary>取某合约最新 tick（内存缓存）。</summary>
    CtpTick? GetLatestTick(string instrumentId);

    /// <summary>取所有已订阅合约最新 tick。</summary>
    IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks();
}
