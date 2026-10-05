using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Sim.Server;

/// <summary>
/// 单个客户端连接：读循环（分发 op）+ 出站通道单写者（顺序保持）+ 捕获缓冲（应答先于推送）。
/// <para>
/// <b>顺序机制（capture-during-dispatch）</b>：全部出站写入经 <c>_writeGate</c>——
/// 分发前置 <c>_dispatching=true</c>；路由线程（核心事件线程）见 dispatching 则把帧入 <c>_captured</c>；
/// 应答帧先 TryWrite，随后在同一锁内排空捕获、复位标志。锁内只有非阻塞 TryWrite，无死锁面。
/// 注意：核心在 op 调用内同步抛事件 ⇒ 该路由就发生在**本连接自己的分发线程**上（自持锁无竞争）；
/// 其它来源（别的连接的 op）在各自线程路由。
/// </para>
/// <para>
/// <b>查询无登录守卫</b>（对齐 Phase 1「未登录查询返回空账本」：绑定空 → AccountIdOf(null)="sim"）；
/// 下单/撤单的「未登录」守卫在客户端（RemoteCtpSession）同步抛——服务端只对路由负责。
/// </para>
/// </summary>
internal sealed class SimConnection
{
    private readonly SimRpcServer _server;
    private readonly SimTradingCore _core;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _writeGate = new();
    private readonly object _stateLock = new();
    private readonly List<byte[]> _captured = new();

    private volatile bool _dispatching;
    private volatile bool _closed;

    /// <summary>当前绑定账户（登录时设置/登出清空；路由线程只读快照）。</summary>
    internal string? AccountId { get; private set; }

    /// <summary>行情订阅集（subscribe 加/unsubscribe 清；tick 路由用）。</summary>
    internal HashSet<string> Subscriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal SimConnection(SimRpcServer server, SimTradingCore core, TcpClient tcp)
    {
        _server = server;
        _core = core;
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>启动读写两个循环（fire-and-forget，各自带 finally 收尾）。</summary>
    internal void Run()
    {
        _ = Task.Run(ReaderLoopAsync);
        _ = Task.Run(WriterLoopAsync);
    }

    // —— 路由（核心事件线程调用） ——

    internal bool MatchesAccount(string accountId)
    {
        lock (_stateLock)
            return string.Equals(AccountId, accountId, StringComparison.OrdinalIgnoreCase);
    }

    internal bool SubscribedTo(string instrumentId)
    {
        lock (_stateLock)
            return Subscriptions.Contains(instrumentId);
    }

    internal void RoutePush(byte[] frame)
    {
        lock (_writeGate)
        {
            if (_closed) return;
            if (_dispatching)
                _captured.Add(frame);   // 本连接 op 执行中 ⇒ 先应答后推送
            else
                _out.Writer.TryWrite(frame);
        }
    }

    // —— 循环 ——

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (!_closed)
            {
                var bytes = await SimFraming.ReadFrameAsync(_stream).ConfigureAwait(false);

                SimRequest? req;
                try { req = JsonSerializer.Deserialize<SimRequest>(bytes, SimJson.Options); }
                catch (JsonException) { break; }   // 坏帧 ⇒ 断连
                if (req is null) break;

                try { await HandleAsync(req).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[CTP-SIM] 连接分发故障（断开）：{ex.Message}");
                    break;
                }
            }
        }
        catch
        {
            // 读取异常（socket 关闭）⇒ 统一 finally 收尾
        }
        finally
        {
            Close();
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync().ConfigureAwait(false))
                await SimFraming.WriteFrameAsync(_stream, frame).ConfigureAwait(false);
        }
        catch
        {
            // 写故障 ⇒ Close 由 finally 统一触发
        }
        finally
        {
            Close();
        }
    }

    // —— op 分发（读循环上串行执行：单连接请求串行化——内存操作 sub-ms，见 CLAUDE.md 已知限制） ——

    private async Task HandleAsync(SimRequest req)
    {
        object? result = null;
        SimWireError? err = null;

        lock (_writeGate) { _dispatching = true; }
        try
        {
            switch (req.Op)
            {
                case SimOps.Hello:
                    result = new SimHello(1);
                    break;

                case SimOps.Login:
                {
                    var login = Arg<LoginArg>(req);
                    if (login is null) { err = Missing("login"); break; }
                    // 先认证后绑定：凭据失败（90017）时连接不得绑上该账户（否则未登录连接即持有账户）
                    var loginResult = _core.Login(login.BrokerId, login.UserId, login.Password);
                    lock (_stateLock) AccountId = SimTradingCore.AccountIdOf(login.UserId);
                    result = loginResult;
                    break;
                }

                case SimOps.Logout:
                    lock (_stateLock) { AccountId = null; Subscriptions.Clear(); }
                    _ = _core.FlushAsync();   // fire-and-forget；绝不动 feed（host 生命周期）
                    break;

                case SimOps.QueryAccount:
                    result = _core.QueryAccount(BoundAccount());
                    break;

                case SimOps.QueryPositions:
                    result = _core.QueryPositions(BoundAccount(), Arg<InstrumentArg>(req)?.InstrumentId);
                    break;

                case SimOps.QueryOrders:
                    result = _core.QueryOrders(BoundAccount(), Arg<InstrumentArg>(req)?.InstrumentId);
                    break;

                case SimOps.QueryInstruments:
                    // 上市表是交易所全局的（非账户过滤）；服务端把 Simulator 类型映射为客户端安全的 Domain DTO
                    result = _core.QueryListedInstruments().Select(i => new CtpInstrument
                    {
                        InstrumentId = i.InstrumentId,
                        ExchangeId = i.ExchangeId,
                        VolumeMultiple = i.VolumeMultiple,
                        PriceTick = i.PriceTick,
                        PreSettlement = i.PreSettlement,
                        BasePrice = i.BasePrice,
                    }).ToList();
                    break;

                case SimOps.Quotes:
                    // 行情快照/时钟（2026-10-05 进程分离：前置行情腿经 wire 取订阅快照与 TradingDay/TimeOfDay——
                    // 替代进程内直调；无快照的合约（未激活行情）跳过，与前置旧语义一致）
                {
                    var arg = Arg<QuoteArg>(req);
                    var quotes = new List<MarketQuote>();
                    if (arg?.Symbols is not null)
                    {
                        foreach (var s in arg.Symbols)
                        {
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            if (_core.Market.GetQuote(s) is not { } q) continue;   // SimQuote 是 struct——模式匹配解 Nullable
                            quotes.Add(new MarketQuote(q.InstrumentId, q.BidPrice, q.AskPrice, q.LastPrice));
                        }
                    }
                    result = new QuoteSnapshot(_core.CurrentTradingDay, _core.CurrentTimeOfDay, quotes);
                    break;
                }

                case SimOps.InsertOrder:
                {
                    var insert = Arg<CtpOrderRequest>(req);
                    if (insert is null) { err = Missing("insertOrder"); break; }
                    result = _core.InsertOrder(BoundAccount(), insert);
                    break;
                }

                case SimOps.CancelOrder:
                {
                    var cancel = Arg<CtpOrderRef>(req);
                    if (cancel is null) { err = Missing("cancelOrder"); break; }
                    _core.CancelOrder(BoundAccount(), cancel);
                    break;
                }

                case SimOps.Subscribe:
                {
                    var sub = Arg<SubscribeArg>(req);
                    if (sub is null) { err = Missing("subscribe"); break; }
                    foreach (var s in sub.Symbols)
                    {
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        // 先登记订阅、再激活——ActivateMarket 会同步外发盘口快照 tick，
                        // 若登记在后，快照路由时本连接的订阅集还没有该合约 ⇒ 订阅者永远收不到这唯一一帧
                        // （事件驱动下空闲市场无后续事件——板恒空，2026-09-28 实测踩中）。
                        // 先登记则快照恰好落在本 op 的 capture-during-dispatch 缓冲里，应答帧后送达（F1 顺序契约）。
                        lock (_stateLock) Subscriptions.Add(s);
                        _core.ActivateMarket(s);   // 订阅即激活行情（激活即推快照）
                    }
                    break;
                }

                case SimOps.Unsubscribe:
                    lock (_stateLock) Subscriptions.Clear();
                    break;

                default:
                    err = new SimWireError(SimWireErrors.Protocol, $"未知操作：{req.Op}");
                    break;
            }
        }
        catch (CtpBusinessException ex)
        {
            err = new SimWireError(ex.ErrorId, ex.ErrorMsg);
        }
        catch (Exception ex)
        {
            err = new SimWireError(SimWireErrors.Protocol, $"仿真柜台内部错误：{ex.Message}");
        }
        finally
        {
            var resp = JsonSerializer.SerializeToUtf8Bytes(
                new SimResponse(
                    req.Id,
                    err is null,
                    err is null ? ToElement(result) : null,
                    err),
                SimJson.Options);
            lock (_writeGate)
            {
                if (!_closed)
                {
                    _out.Writer.TryWrite(resp);
                    // 先应答后推送：本 op 执行期间路由进来的帧在此排空（F1 顺序保证）
                    foreach (var f in _captured) _out.Writer.TryWrite(f);
                }
                _captured.Clear();
                _dispatching = false;
            }
        }
    }

    /// <summary>绑定账户（未登录 → "sim"，对齐 Phase 1 未登录查询语义）。</summary>
    private string BoundAccount()
    {
        lock (_stateLock) return AccountId ?? SimTradingCore.AccountIdOf(null);
    }

    private static T? Arg<T>(SimRequest req) where T : class
        => req.Arg is { } a ? a.Deserialize<T>(SimJson.Options) : null;

    private static SimWireError Missing(string op)
        => new(SimWireErrors.Protocol, $"{op} 缺少参数。");

    private static JsonElement? ToElement(object? result)
        => result is null ? null : JsonSerializer.SerializeToElement(result, SimJson.Options);

    // —— 关闭 ——

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _out.Writer.TryComplete(); } catch { }
        lock (_writeGate) _captured.Clear();
        try { _stream.Close(); } catch { }
        try { _tcp.Close(); } catch { }
        lock (_stateLock) { AccountId = null; Subscriptions.Clear(); }
        _server.RemoveConnection(this);
    }
}
