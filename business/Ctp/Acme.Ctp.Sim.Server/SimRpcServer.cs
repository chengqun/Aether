using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Sim.Server;

/// <summary>
/// 仿真柜台 RPC 服务端：host 内**单个** <see cref="SimTradingCore"/> 服务所有连接，按 accountId 路由推送。
/// <para>
/// <b>路由规则</b>：订单/成交回报 → 该账户的连接（广播给全部同账户连接——见「一账户一客户端」已知限制）；
/// tick → 订阅集包含该合约的连接（仅 ExposeSyntheticTicks，即 AETHER_CTP_SIM_MD=local）。
/// </para>
/// <para>
/// <b>顺序保证（关键）</b>：<see cref="SimTradingCore.InsertOrder"/> 在返回前同步抛出订单/成交事件——
/// 朴素直写会让推送先于应答到达客户端，而 <c>CtpBrokerage</c> 在 <c>OrderInsertAsync</c> 应答后才把
/// OrderRef 注册进 <c>CtpOrderTracker</c>，先到的成交回报会被静默丢弃（10s 看门狗误判 Invalid）。
/// 真 CTP 的语义是「rc 返回先于 SPI 回调」，线上由 <see cref="SimConnection"/> 的
/// capture-during-dispatch 复刻：本连接的 op 执行期间产生的推送先入捕获缓冲，应答帧写入后再排空。
/// 其它来源（行情定时器、别的连接的 op）对本连接的推送无此契约，直写出站通道。
/// </para>
/// <para>
/// <b>不做的事</b>：不 dispose core（host 拥有核心生命周期）；不调 <c>core.Logout()</c>
/// （行情 feed 生命周期 = host 生命周期，单客户端登出不得停全局 feed）；不做账户租约/踢人
/// （同账户双客户端会撞 OrderRef 序号——已知限制，文档化）。
/// </para>
/// </summary>
public sealed class SimRpcServer : IDisposable
{
    private readonly SimTradingCore _core;
    private readonly bool _exposeTicks;
    private readonly string _host;
    private readonly int _port;
    private readonly List<SimConnection> _connections = new();
    private readonly object _registryLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private bool _disposed;

    public SimRpcServer(SimTradingCore core, bool exposeTicks, string host, int port)
    {
        _core = core;
        _exposeTicks = exposeTicks;
        _host = host;
        _port = port;

        _core.OrderChanged += OnOrderChanged;
        _core.TradeFilled += OnTradeFilled;
        _core.TickProduced += OnTickProduced;
    }

    /// <summary>实际监听端点（<c>port=0</c> 时为内核分配的临时端口，测试用）。</summary>
    public IPEndPoint? LocalEndpoint { get; private set; }

    /// <summary>开始监听。幂等。</summary>
    public void Start()
    {
        if (_listener is not null) return;
        var addr = _host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(_host);
        var listener = new TcpListener(addr, _port);
        listener.Start(backlog: 16);
        _listener = listener;
        LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
        _acceptCts = new CancellationTokenSource();
        var token = _acceptCts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
    }

    /// <summary>停 accept、关全部连接（不 dispose core——host 拥有）。</summary>
    public async Task StopAsync()
    {
        _acceptCts?.Cancel();
        try { _listener?.Stop(); } catch { }

        SimConnection[] conns;
        lock (_registryLock) conns = _connections.ToArray();
        foreach (var c in conns) c.Close();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
    }

    internal void RemoveConnection(SimConnection connection)
    {
        lock (_registryLock) _connections.Remove(connection);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }   // listener 已停

            var conn = new SimConnection(this, _core, _exposeTicks, tcp);
            lock (_registryLock) _connections.Add(conn);
            conn.Run();
        }
    }

    // —— 核心事件 → 连接路由（payload 只序列化一次，跨连接复用同一帧字节） ——

    private void OnOrderChanged(string accountId, CtpOrder order) => Broadcast(accountId, SimEvents.Order, order);

    private void OnTradeFilled(string accountId, CtpTrade trade) => Broadcast(accountId, SimEvents.Trade, trade);

    private void OnTickProduced(CtpTick tick)
    {
        if (!_exposeTicks) return;
        byte[]? frame = null;
        SimConnection[] conns;
        lock (_registryLock) conns = _connections.ToArray();
        foreach (var c in conns)
        {
            if (!c.SubscribedTo(tick.InstrumentId)) continue;
            frame ??= EncodePush(SimEvents.Tick, tick);
            c.RoutePush(frame);
        }
    }

    private void Broadcast(string accountId, string ev, object payload)
    {
        byte[]? frame = null;
        SimConnection[] conns;
        lock (_registryLock) conns = _connections.ToArray();
        foreach (var c in conns)
        {
            if (!c.MatchesAccount(accountId)) continue;
            frame ??= EncodePush(ev, payload);
            c.RoutePush(frame);
        }
    }

    private static byte[] EncodePush(string ev, object payload)
        => JsonSerializer.SerializeToUtf8Bytes(
            new SimPush(ev, JsonSerializer.SerializeToElement(payload, SimJson.Options)),
            SimJson.Options);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.OrderChanged -= OnOrderChanged;
        _core.TradeFilled -= OnTradeFilled;
        _core.TickProduced -= OnTickProduced;
        StopAsync().GetAwaiter().GetResult();
    }
}
