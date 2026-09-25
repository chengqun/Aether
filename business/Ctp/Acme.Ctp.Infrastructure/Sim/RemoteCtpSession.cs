using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
using Ctp.Net.Core.MarketData;

namespace Acme.Ctp.Infrastructure.Sim;

/// <summary>
/// 本地仿真柜台的**远程**会话适配器：把独立 host 进程（<c>Acme.Ctp.Sim.Host</c>，经
/// <see cref="SimRpcClient"/> 连 127.0.0.1）接到 <see cref="ICtpSession"/> 接缝上，
/// 使 Application / 6 个 CTP AI 工具 / 桌面「期货」页 / Lean 的 <c>CtpBrokerage</c> **全部零改动**。
/// <para>
/// 结构镜像 Phase 1 的 <c>SimCtpSession</c>（进程内版）：泵 Channel + 后台回调线程、
/// tick 本地缓存、状态去重、未登录同步抛。差别只在撮合/账本/行情引擎全部住在 host 进程——
/// 客户端进程不再携带仿真核心（Infrastructure 对 Simulator 的引用已删除）。
/// </para>
/// <para>
/// <b>Md（行情）模式唯一权威在 host</b>：Hello 握手带回 <c>exposeTicks</c>
/// （host 环境变量 <c>AETHER_CTP_SIM_MD=local</c>）。默认 false ⇒ 本类内部仍持一个真实
/// <see cref="CtpMarketData"/> 走真实前置（与 Phase 1 相同）；true ⇒ 完全离线，tick 由 host 推送。
/// </para>
/// <para>
/// <b>与 Phase 1 的契约差异（消费方影响已核实）</b>：服务端风控拒单从「Task 返回前同步抛」
/// 变为「await 时抛」（VM/AI 工具 await+catch、Lean GetResult 全兼容）；「未登录」守卫保留
/// **客户端本地同步抛**；host 未启动 ⇒ <c>CtpBusinessException(90012, 含启动命令)</c>；
/// host 死亡 ⇒ <see cref="ICtpSession.ConnectionStateChanged"/> 发 Disconnected、后续操作快速失败 90013。
/// 无自动重连——重新登录即重连。
/// </para>
/// </summary>
public sealed class RemoteCtpSession : ICtpSession, IDisposable
{
    /// <summary>与 Simulator.SimErrorCodes.NotLoggedIn 同码值（分层：客户端不引 Simulator，码值保持一致）。</summary>
    private const int NotLoggedIn = 90001;

    /// <summary>等待盘面回调的队列上限（超出丢最旧帧——与 Phase 1 相同的背压策略）。</summary>
    private const int CallbackQueueCapacity = 4096;

    private readonly SimRpcClient _client;
    private readonly object _lock = new();
    private readonly Channel<Callback> _pump = Channel.CreateBounded<Callback>(
        new BoundedChannelOptions(CallbackQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private CtpLoginResult? _loginResult;
    private string? _brokerId;
    private string? _userId;
    private CtpConnectionStatus _status = CtpConnectionStatus.Disconnected;
    private bool _exposeTicks;          // Hello 结果；连接断开即失效
    private bool _disposed;

    // —— 行情：默认真实前置，ExposeTicks 时改由 host 推送 ——
    private CtpMarketData? _md;
    private MdCreds? _cachedMdCreds;
    private readonly List<string> _cachedSymbols = new();
    private readonly ConcurrentDictionary<string, CtpTick> _latestTicks = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _mdSocketAlive;

    private sealed record MdCreds(string Front, string BrokerId, string UserId, string Password);

    /// <summary>泵队列元素：一次外发（报单回报 / 成交回报 / 行情 / 连接状态）。</summary>
    private readonly record struct Callback(
        CtpOrder? Order = null,
        CtpTrade? Trade = null,
        CtpTick? Tick = null,
        CtpConnectionStatus? Status = null);

    public RemoteCtpSession(string host, int port)
    {
        _client = new SimRpcClient(host, port);
        _client.OrderPushed += order => Enqueue(new Callback(Order: order));
        _client.TradePushed += trade => Enqueue(new Callback(Trade: trade));
        _client.TickPushed += tick => Enqueue(new Callback(Tick: tick));
        // host 死亡/超时销毁：清登录态 + Disconnected（下次 LoginAsync 重连）
        _client.ConnectionLost += OnConnectionLost;
        _ = Task.Run(PumpAsync);
    }

    // —— ICtpSession：状态 ——

    public bool IsConnected { get { lock (_lock) return _loginResult is not null; } }

    public bool IsMdSubscribed
    {
        get
        {
            lock (_lock)
                return _exposeTicks && _client.IsAlive || (_md is not null && !_md.IsDisposed);
        }
    }

    public CtpLoginResult? LoginResult { get { lock (_lock) return _loginResult; } }

    public string? BrokerId { get { lock (_lock) return _brokerId; } }

    public string? UserId { get { lock (_lock) return _userId; } }

    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpTick>? TickReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    // —— ICtpSession：登录 / 登出 ——

    /// <summary>
    /// 仿真登录：host 不校验凭据（仿真的目的是「连得上」），返回可被 <c>int.Parse</c> 解析的
    /// <c>MaxOrderRef</c>。host 未启动 ⇒ <c>CtpBusinessException(90012)</c>，消息带启动命令。
    /// </summary>
    public async Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default)
    {
        EnsureNotDisposed();

        // 连接 + Hello 握手（已连接则直通；死连接则重建）。MD 模式以 Hello 为准。
        var hello = await _client.ConnectAsync(ct).ConfigureAwait(false);
        lock (_lock) _exposeTicks = hello.ExposeTicks;

        var result = await _client.LoginAsync(new LoginArg(request.BrokerId, request.UserId), ct).ConfigureAwait(false);
        lock (_lock)
        {
            _loginResult = result;
            _brokerId = request.BrokerId;
            _userId = request.UserId;
        }
        SetStatus(CtpConnectionStatus.Connected);
        return result;
    }

    /// <summary>登出：保留 host 侧账本（下次登录续用），本地只清会话状态与行情连接。落盘由 host 侧兜底。</summary>
    public void Logout()
    {
        UnsubscribeMarketData();
        lock (_lock)
        {
            _loginResult = null;
            _brokerId = null;
            _userId = null;
        }
        _client.Logout();   // fire-and-forget：服务端清绑定/订阅 + flush
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    // —— ICtpSession：查询 ——

    /// <summary>
    /// 查询无登录守卫（Phase 1 接缝契约④）：未登录 ⇒ 本地直答**空账本形态**（不起 RPC——
    /// 远端未连接会抛 90013，而契约要求空壳不抛异常）。登录后走 RPC（服务端按绑定账户取数）。
    /// </summary>
    public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult(new CtpAccount { AccountId = AccountKey(_userId) });
        }
        return _client.QueryAccountAsync(ct);
    }

    public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult<IReadOnlyList<CtpPosition>>(Array.Empty<CtpPosition>());
        }
        return _client.QueryPositionsAsync(instrumentId, ct);
    }

    public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult<IReadOnlyList<CtpOrder>>(Array.Empty<CtpOrder>());
        }
        return _client.QueryOrdersAsync(instrumentId, ct);
    }

    /// <summary>与 Simulator.SimTradingCore.AccountIdOf 同一归一规则（空 → "sim"；分层：客户端不引 Simulator，规则复制+注释交叉引用）。</summary>
    private static string AccountKey(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? "sim" : userId;

    // —— ICtpSession：下单 / 撤单 ——

    /// <summary>
    /// 下单：**fire-and-forget**——返回 <c>OrderRef</c>/<c>RequestId</c>，订单状态经
    /// <see cref="OrderReceived"/> 异步外发（服务端保证应答先于本单推送——Lean 的
    /// <c>CtpOrderTracker</c> 在应答后才注册 OrderRef）。「未登录」本地同步抛；
    /// 服务端风控拒单在 await 时抛 <see cref="CtpBusinessException"/>（ErrorId 原样）。
    /// </summary>
    public Task<CtpOrderInsertResult> OrderInsertAsync(CtpOrderRequest request, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        return _client.InsertOrderAsync(request, ct);
    }

    /// <summary>撤单：定位不到/已终态由服务端抛 <see cref="CtpBusinessException"/>（await 时到达）。</summary>
    public Task OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        return _client.CancelOrderAsync(orderRef, ct);
    }

    // —— ICtpSession：行情 ——

    /// <summary>
    /// 订阅行情。host 的 Hello 回答模式：默认走**真实**行情前置（与 <c>CtpSessionService</c>
    /// 同款增量订阅语义）；<c>exposeTicks</c>（host 侧 AETHER_CTP_SIM_MD=local）时完全离线，
    /// 只把订阅集告知 host（host 订阅即激活随机游走 + 按订阅路由 tick 推送）。
    /// </summary>
    public async Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        EnsureNotDisposed();

        // 模式必须先经 Hello 得知（行情前置独立于交易登录，可能先于 LoginAsync 调用）
        var hello = await _client.ConnectAsync(ct).ConfigureAwait(false);
        lock (_lock) _exposeTicks = hello.ExposeTicks;

        if (hello.ExposeTicks)
        {
            lock (_lock) _cachedSymbols.AddRange(symbols.Where(s => !_cachedSymbols.Contains(s)));
            await _client.SubscribeAsync(symbols, ct).ConfigureAwait(false);
            return;
        }

        List<string> toSubscribe;
        bool needRebuild;
        lock (_lock)
        {
            var creds = new MdCreds(connect.MdFrontAddress, connect.BrokerId, connect.UserId, connect.Password);
            if (_md is not null && _mdSocketAlive && _cachedMdCreds == creds)
            {
                // 已连接且同一账户：增量订阅（不重建，避免覆盖已有订阅——如期货页全量 + 实盘 ag2610 累加）
                toSubscribe = symbols.Where(s => !_cachedSymbols.Contains(s)).ToList();
                _cachedSymbols.AddRange(toSubscribe);
                needRebuild = false;
            }
            else
            {
                if (_md is not null)
                {
                    _md.TickReceived -= OnMdTick;
                    _md.Dispose();
                }
                var flow = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctp_md_flow", Guid.NewGuid().ToString("N")[..8]);
                System.IO.Directory.CreateDirectory(flow);
                _md = new CtpMarketData(flow);
                _md.TickReceived += OnMdTick;
                _cachedMdCreds = creds;
                _cachedSymbols.Clear();
                _cachedSymbols.AddRange(symbols);
                toSubscribe = symbols.ToList();
                needRebuild = true;
            }
        }

        if (needRebuild)
        {
            await _md!.LoginAsync(connect.MdFrontAddress, connect.BrokerId, connect.UserId, connect.Password).ConfigureAwait(false);
            await _md.SubscribeMarketDataAsync(toSubscribe).ConfigureAwait(false);
            _mdSocketAlive = true;
        }
        else if (toSubscribe.Count > 0)
        {
            await _md!.SubscribeMarketDataAsync(toSubscribe).ConfigureAwait(false);
        }
    }

    public void UnsubscribeMarketData()
    {
        lock (_lock)
        {
            if (_md is not null)
            {
                _md.TickReceived -= OnMdTick;
                _md.Dispose();
            }
            _md = null;
            _cachedMdCreds = null;
            _cachedSymbols.Clear();
            _mdSocketAlive = false;
            if (!_exposeTicks) _latestTicks.Clear();
        }
        if (_client.IsAlive) _client.Unsubscribe();   // fire-and-forget
    }

    public CtpTick? GetLatestTick(string instrumentId)
        => _latestTicks.TryGetValue(instrumentId, out var t) ? t : null;

    public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks()
        => new Dictionary<string, CtpTick>(_latestTicks);

    // —— 内部：回调外发 ——

    /// <summary>入队（非阻塞；队满由 Channel 丢最旧帧）。</summary>
    private void Enqueue(Callback cb) => _pump.Writer.TryWrite(cb);

    /// <summary>后台泵：在非调用线程上依次外发（复刻真柜台「回报来自 SDK 内部线程」的语义）。</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _pump.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_pump.Reader.TryRead(out var cb))
                {
                    try
                    {
                        if (cb.Order is not null) OrderReceived?.Invoke(this, cb.Order);
                        if (cb.Trade is not null) TradeReceived?.Invoke(this, cb.Trade);
                        if (cb.Tick is not null)
                        {
                            // 离线行情（AETHER_CTP_SIM_MD=local）没有 OnMdTick 那条路，缓存必须在这里写——
                            // 否则 GetLatestTick/GetAllLatestTicks 恒为 null，「期货」页拿不到任何报价。
                            _latestTicks[cb.Tick.InstrumentId] = cb.Tick;
                            TickReceived?.Invoke(this, cb.Tick);
                        }
                        if (cb.Status is { } s) ConnectionStateChanged?.Invoke(this, s);
                    }
                    catch (Exception ex)
                    {
                        // 订阅方（UI/Lean）抛异常不得终止泵——否则后续回报全部静默丢失
                        System.Diagnostics.Trace.WriteLine($"[CTP-SIM] 回调订阅方异常（已忽略）：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CTP-SIM] 回调泵退出：{ex.Message}");
        }
    }

    private void OnMdTick(object? sender, TickData tick)
    {
        var dto = CtpSessionMapper.ToDto(tick);
        _latestTicks[dto.InstrumentId] = dto;
        Enqueue(new Callback(Tick: dto));
    }

    private void OnConnectionLost()
    {
        lock (_lock)
        {
            _loginResult = null;
            _brokerId = null;
            _userId = null;
            _exposeTicks = false;
        }
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    private void SetStatus(CtpConnectionStatus status)
    {
        lock (_lock)
        {
            if (_status == status) return;
            _status = status;
        }
        Enqueue(new Callback(Status: status));
    }

    private void EnsureLoggedIn()
    {
        lock (_lock)
        {
            if (_loginResult is null)
                throw new CtpBusinessException(NotLoggedIn, "未登录，无法交易。");
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteCtpSession));
    }

    /// <summary>释放（幂等）：停泵 + 断行情 + 关 RPC 连接。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _pump.Writer.TryComplete();
        try { UnsubscribeMarketData(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
