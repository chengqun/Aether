using System;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Sim;

namespace Acme.Ctp.Infrastructure.Sim;

/// <summary>
/// 「用前置地址选柜台」的路由会话：前置地址填 <c>sim</c>（或默认仿真端点 <c>127.0.0.1:17800</c>，
/// 判据见 <see cref="SimAddress.IsSimFront"/>）⇒ 登录本地仿真柜台（<see cref="RemoteCtpSession"/>，
/// 连独立 host 进程）；填真实前置 ⇒ 官方链路（<see cref="CtpSessionService"/>）。
/// <para>
/// <b>这是唯一的 <see cref="ICtpSession"/> 注册</b>——上层（Application / 6 个 AI 工具 / 期货页 /
/// Lean <c>CtpBrokerage</c>）照旧只认 <see cref="ICtpSession"/>，柜台选择对它们不可见。
/// 镜像股票页 <c>RoutingStockBrokerage</c>（FlaUI/MCP 运行时切换）的模式：两个后端常驻，
/// 按登录请求逐次路由，切柜台 = 换前置地址重新登录，<b>无需环境变量、无需重启 app</b>。
/// 地址随凭据存进 ctp.db ⇒ 自动登录自动记住上次用的柜台。
/// </para>
/// <para>
/// <b>事件转发</b>：两个后端的四个事件都在构造期订阅并原样转发——同一时刻只有被登录的后端会发事件
/// （切柜台时旧后端先被登出），订阅方拿到的 sender 恒为本路由对象。
/// </para>
/// <para>
/// <b>代价（诚实说）</b>：真实路径多一层委托（行为不变、不再是「逐字节同一条注册路径」）——
/// 与所有运行时可切后端的架构一致，股票页已付过同款。
/// </para>
/// </summary>
public sealed class RoutingCtpSession : ICtpSession, IDisposable
{
    private readonly CtpSessionService _real;
    private readonly RemoteCtpSession _sim;
    private volatile ICtpSession _active;

    public RoutingCtpSession(CtpSessionService real, RemoteCtpSession sim)
    {
        _real = real;
        _sim = sim;
        _active = real;

        // 两个后端的事件都转发（路由后哪个连着哪个才会发；切柜台时旧的先登出）
        _real.OrderReceived += (_, o) => OrderReceived?.Invoke(this, o);
        _real.TradeReceived += (_, t) => TradeReceived?.Invoke(this, t);
        _real.TickReceived += (_, k) => TickReceived?.Invoke(this, k);
        _real.ConnectionStateChanged += (_, s) => ConnectionStateChanged?.Invoke(this, s);

        _sim.OrderReceived += (_, o) => OrderReceived?.Invoke(this, o);
        _sim.TradeReceived += (_, t) => TradeReceived?.Invoke(this, t);
        _sim.TickReceived += (_, k) => TickReceived?.Invoke(this, k);
        _sim.ConnectionStateChanged += (_, s) => ConnectionStateChanged?.Invoke(this, s);
    }

    /// <summary>当前活跃后端是否仿真柜台（测试/诊断用；初始为 false=真实）。</summary>
    public bool IsSimulationActive => ReferenceEquals(_active, _sim);

    // —— ICtpSession：状态（全部读活跃后端） ——

    public bool IsConnected => _active.IsConnected;

    public bool IsMdSubscribed => _active.IsMdSubscribed;

    public CtpLoginResult? LoginResult => _active.LoginResult;

    public string? BrokerId => _active.BrokerId;

    public string? UserId => _active.UserId;

    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpTick>? TickReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    // —— ICtpSession：登录（路由决策点） ——

    /// <summary>
    /// 按前置地址路由：<see cref="SimAddress.IsSimFront"/> ⇒ 仿真，否则 ⇒ 真实。
    /// 切柜台时若旧后端还连着，先登出它（发 Disconnected 状态，UI 语义连贯）。
    /// </summary>
    public async Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default)
    {
        ICtpSession target = SimAddress.IsSimFront(request.FrontAddress) ? _sim : _real;

        if (!ReferenceEquals(_active, target) && _active.IsConnected)
        {
            try { _active.Logout(); }
            catch { /* 旧会话登出失败不阻断新登录 */ }
        }
        _active = target;

        return await target.LoginAsync(request, ct).ConfigureAwait(false);
    }

    public void Logout() => _active.Logout();

    // —— ICtpSession：其余成员（委托活跃后端） ——

    public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
        => _active.QueryAccountAsync(ct);

    public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
        => _active.QueryPositionAsync(instrumentId, ct);

    public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default)
        => _active.QueryOrderAsync(instrumentId, ct);

    public Task<CtpOrderInsertResult> OrderInsertAsync(CtpOrderRequest request, CancellationToken ct = default)
        => _active.OrderInsertAsync(request, ct);

    public Task OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct = default)
        => _active.OrderActionAsync(orderRef, ct);

    public Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default)
        => _active.SubscribeMarketDataAsync(connect, symbols, ct);

    public void UnsubscribeMarketData() => _active.UnsubscribeMarketData();

    public CtpTick? GetLatestTick(string instrumentId) => _active.GetLatestTick(instrumentId);

    public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks() => _active.GetAllLatestTicks();

    /// <summary>释放两个后端（幂等各自保证）。</summary>
    public void Dispose()
    {
        try { _sim.Dispose(); } catch { }
        try { _real.Dispose(); } catch { }
    }
}
