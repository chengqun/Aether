using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Sim;

/// <summary>
/// 仿真柜台 RPC 客户端（薄传输半边）：请求-应答相关 + 服务端推送接收。
/// <para>
/// <b>线程与异常语义</b>：推送（订单/成交/tick）在接收线程上以事件形式发出（复刻真柜台「回报来自 SDK 内部线程」）；
/// 服务端业务错误在应答帧里带 <c>err{code,msg}</c>，此处重建为 <see cref="CtpBusinessException"/>——
/// ErrorId 精度是 <c>ICtpSession</c> 接缝的既定契约（测试与消费方都断言 ErrorId），绝不能降级成 IOException 泛型。
/// </para>
/// <para>
/// <b>超时与取消</b>：内部 15s 超时后连接状态不可信 ⇒ 销毁连接（迟到应答按 id 查表 miss 安全丢弃，id 单调不复用）；
/// 调用方取消只取消该请求，连接保留。连接被拒包装为 <c>CtpBusinessException(NotRunning, 含启动命令)</c>。
/// </para>
/// <para>
/// <b>不做自动重连</b>（镜像 Phase 1「本地无网络可断」的诚实度——现在 host 死亡时会断，由上层重新登录触发重连）。
/// </para>
/// </summary>
public sealed class SimRpcClient : IDisposable
{
    private const int DefaultTimeoutMs = 15_000;
    private const int ConnectTimeoutMs = 5_000;

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _connectGate = new(1, 1);   // 连接单飞（VM 登录与自动订阅可能并发触发）
    private readonly SemaphoreSlim _writeGate = new(1, 1);     // 串行化帧写入（并发 CallAsync 不得交错写坏帧）
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly object _teardownLock = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _receiveLoop;
    private long _nextId;
    private volatile bool _alive;
    private bool _disposed;
    private int _lostRaised;

    public SimRpcClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>Hello 结果（连接成功后有效；断线后为 null，需重新连接）。</summary>
    public SimHello? Hello { get; private set; }

    public bool IsAlive => _alive && !_disposed;

    /// <summary>报单回报推送（接收线程上触发）。</summary>
    public event Action<CtpOrder>? OrderPushed;

    /// <summary>成交回报推送（接收线程上触发）。</summary>
    public event Action<CtpTrade>? TradePushed;

    /// <summary>tick 推送（接收线程上触发；host 按订阅集路由——sim 会话行情随柜台恒推送）。</summary>
    public event Action<CtpTick>? TickPushed;

    /// <summary>连接断开（host 死亡/超时销毁；主动 Dispose 不触发）。</summary>
    public event Action? ConnectionLost;

    // —— 连接 ——

    /// <summary>
    /// 连接 + Hello 握手。已连接直接返回；连接失败/被拒包装为
    /// <c>CtpBusinessException(NotRunning, 含启动命令提示)</c>。单飞：并发调用只连一次。
    /// </summary>
    public async Task<SimHello> ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsAlive) return Hello!;

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAlive) return Hello!;

            TearDown();   // 清理上次断连的残留 socket（不发事件——是重建不是故障通知）

            var tcp = new TcpClient();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ConnectTimeoutMs);
                await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                tcp.Dispose();
                if (ct.IsCancellationRequested) throw;   // 调用方主动取消，原样上抛
                throw new CtpBusinessException(SimWireErrors.NotRunning,
                    $"本地仿真柜台未启动（{_host}:{_port} 无法连接）：{ex.Message}。" +
                    "启动命令：dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host");
            }

            _tcp = tcp;
            _stream = tcp.GetStream();
            _alive = true;
            _lostRaised = 0;
            _receiveLoop = Task.Run(ReceiveLoopAsync);

            var hello = await CallCoreAsync<SimHello>(SimOps.Hello, null, ct).ConfigureAwait(false);
            Hello = hello ?? throw new CtpBusinessException(SimWireErrors.Protocol, "仿真柜台 Hello 握手返回空。");
            return Hello;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // —— 请求-应答 ——

    /// <summary>
    /// 请求-应答调用。未连接/断线 ⇒ <c>CtpBusinessException(ConnectionLost)</c>（上层应先 <see cref="ConnectAsync"/>）；
    /// 内部超时 ⇒ <c>Timeout</c> 并销毁连接；服务端错误 ⇒ 原样重建的 <see cref="CtpBusinessException"/>。
    /// </summary>
    public async Task<T?> CallAsync<T>(string op, object? arg, CancellationToken ct = default) where T : class
    {
        ThrowIfDisposed();
        if (!IsAlive)
            throw new CtpBusinessException(SimWireErrors.ConnectionLost, "与本地仿真柜台的连接已断开，请重新登录。");
        return await CallCoreAsync<T>(op, arg, ct).ConfigureAwait(false);
    }

    private async Task<T?> CallCoreAsync<T>(string op, object? arg, CancellationToken ct) where T : class
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = EncodeRequest(id, op, arg);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(tcs);
        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SimFraming.WriteFrameAsync(_stream!, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DefaultTimeoutMs);
            try
            {
                var result = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return result is null ? null : result.Value.Deserialize<T>(SimJson.Options);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 内部超时：连接状态不可信 ⇒ 销毁连接（迟到应答按 id 查表 miss 丢弃）
                TearDownWithEvent();
                throw new CtpBusinessException(SimWireErrors.Timeout,
                    $"等待仿真柜台应答超时（{DefaultTimeoutMs / 1000}s）：op={op}。连接已重置，请重新登录。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方主动取消：只放弃本请求，连接保留
            throw;
        }
        catch (Exception ex) when (ex is not CtpBusinessException)
        {
            // 写帧/读连接故障
            TearDownWithEvent();
            throw new CtpBusinessException(SimWireErrors.ConnectionLost, $"与仿真柜台的连接故障：{ex.Message}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// fire-and-forget 发送（Logout/Unsubscribe 这类同步 void 契约用）：带 id 但不登记应答表，
    /// 迟到的应答在接收循环按 id 查表 miss 安全丢弃。发送失败只 Trace。
    /// </summary>
    public void SendFireAndForget(string op, object? arg)
    {
        if (!IsAlive) return;
        try
        {
            var payload = EncodeRequest(Interlocked.Increment(ref _nextId), op, arg);
            _writeGate.Wait();
            try { SimFraming.WriteFrame(_stream!, payload); }
            finally { _writeGate.Release(); }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CTP-SIM] fire-and-forget 发送失败（已忽略）：{ex.Message}");
        }
    }

    // —— 类型化包装（RemoteCtpSession 用） ——

    public Task<CtpLoginResult> LoginAsync(LoginArg arg, CancellationToken ct = default)
        => NonNullAsync<CtpLoginResult>(SimOps.Login, arg, ct);

    public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
        => NonNullAsync<CtpAccount>(SimOps.QueryAccount, null, ct);

    public async Task<IReadOnlyList<CtpPosition>> QueryPositionsAsync(string? instrumentId, CancellationToken ct = default)
        => await NonNullAsync<List<CtpPosition>>(SimOps.QueryPositions, new InstrumentArg(instrumentId), ct)
           .ConfigureAwait(false);

    public async Task<IReadOnlyList<CtpOrder>> QueryOrdersAsync(string? instrumentId, CancellationToken ct = default)
        => await NonNullAsync<List<CtpOrder>>(SimOps.QueryOrders, new InstrumentArg(instrumentId), ct)
           .ConfigureAwait(false);

    /// <summary>查询 host 上市合约表（「行情随柜台」的行情板数据源；无登录守卫——与其它查询一致）。</summary>
    /// <summary>
    /// 提币（批 4 接线，2026-10-08）：须先 <see cref="LoginAsync"/> 绑定账户（绑定账户即出金账户）——
    /// venue 先烧现货持仓再 custody 签名链上出金。返回链上交易哈希。
    /// 服务端 90019 = 桥未配置 / 持仓不足 / 链拒绝（消息透传）。
    /// </summary>
    public async Task<string> WithdrawAsync(string toAddressHex, ulong amountAtoms, CancellationToken ct = default)
    {
        var r = await CallAsync<WithdrawResult>(SimOps.Withdraw, new WithdrawArg(toAddressHex, amountAtoms), ct).ConfigureAwait(false);
        return r?.TxHash ?? throw new CtpBusinessException(SimWireErrors.Protocol, "提币应答缺少结果。");
    }

    public async Task<IReadOnlyList<CtpInstrument>> QueryInstrumentsAsync(CancellationToken ct = default)
        => await NonNullAsync<List<CtpInstrument>>(SimOps.QueryInstruments, null, ct)
           .ConfigureAwait(false);

    /// <summary>行情快照/时钟（2026-10-05 进程分离：前置行情腿的订阅快照与 TradingDay/TimeOfDay 来源）。
    /// symbols null/空 = 只取时钟。</summary>
    public Task<QuoteSnapshot> GetQuotesAsync(IReadOnlyList<string>? symbols, CancellationToken ct = default)
        => NonNullAsync<QuoteSnapshot>(SimOps.Quotes, new QuoteArg(symbols), ct);

    public Task<CtpOrderInsertResult> InsertOrderAsync(CtpOrderRequest request, CancellationToken ct = default)
        => NonNullAsync<CtpOrderInsertResult>(SimOps.InsertOrder, request, ct);

    public Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default)
        => CallAsync<object>(SimOps.CancelOrder, orderRef, ct);

    public Task SubscribeAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
        => CallAsync<object>(SimOps.Subscribe, new SubscribeArg(symbols), ct);

    /// <summary>登出（fire-and-forget：服务端清绑定/订阅并 flush；本地不等）。</summary>
    public void Logout() => SendFireAndForget(SimOps.Logout, null);

    /// <summary>退订（fire-and-forget）。</summary>
    public void Unsubscribe() => SendFireAndForget(SimOps.Unsubscribe, null);

    private async Task<T> NonNullAsync<T>(string op, object? arg, CancellationToken ct) where T : class
        => (await CallAsync<T>(op, arg, ct).ConfigureAwait(false))
           ?? throw new CtpBusinessException(SimWireErrors.Protocol, $"仿真柜台对 {op} 返回了空应答。");

    private static byte[] EncodeRequest(long id, string op, object? arg)
        => JsonSerializer.SerializeToUtf8Bytes(
            new SimRequest(id, op, arg is null ? null : JsonSerializer.SerializeToElement(arg, SimJson.Options)),
            SimJson.Options);

    // —— 接收循环 ——

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var bytes = await SimFraming.ReadFrameAsync(_stream!).ConfigureAwait(false);

                SimFrame? frame;
                try { frame = JsonSerializer.Deserialize<SimFrame>(bytes, SimJson.Options); }
                catch (JsonException) { break; }   // 坏帧 ⇒ 断连
                if (frame is null) break;

                if (frame.Ev is not null)
                {
                    DispatchPush(frame);
                    continue;
                }

                if (frame.Id is long id && _pending.TryGetValue(id, out var p))
                {
                    if (frame.Ok == true) p.Tcs.TrySetResult(frame.Result);
                    else if (frame.Err is { } e)
                        p.Tcs.TrySetException(new CtpBusinessException(e.Code, e.Msg));
                    else
                        p.Tcs.TrySetException(new CtpBusinessException(SimWireErrors.Protocol, "仿真柜台返回了无错误的失败应答。"));
                }
                // id 查表 miss（超时已放弃 / fire-and-forget 的迟到应答）⇒ 安全丢弃
            }
        }
        catch
        {
            // 读取异常（socket 关闭/帧损坏）⇒ 收尾统一走 finally
        }
        finally
        {
            TearDownWithEvent();
        }
    }

    private void DispatchPush(SimFrame frame)
    {
        try
        {
            switch (frame.Ev)
            {
                case SimEvents.Order when frame.Arg is { } o:
                    OrderPushed?.Invoke(o.Deserialize<CtpOrder>(SimJson.Options)!);
                    break;
                case SimEvents.Trade when frame.Arg is { } t:
                    TradePushed?.Invoke(t.Deserialize<CtpTrade>(SimJson.Options)!);
                    break;
                case SimEvents.Tick when frame.Arg is { } k:
                    TickPushed?.Invoke(k.Deserialize<CtpTick>(SimJson.Options)!);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 解码失败/订阅方异常不得杀死接收循环——否则后续应答全部悬挂
            Trace.WriteLine($"[CTP-SIM] 推送解码/订阅方异常（已忽略）：{ex.Message}");
        }
    }

    // —— 生命周期 ——

    private void TearDown()
    {
        lock (_teardownLock)
        {
            _alive = false;
            Hello = null;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
            foreach (var kv in _pending)
                kv.Value.Tcs.TrySetException(new CtpBusinessException(
                    SimWireErrors.ConnectionLost, "与本地仿真柜台的连接已断开。"));
            _pending.Clear();
        }
    }

    private void TearDownWithEvent()
    {
        TearDown();
        if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
        {
            try { ConnectionLost?.Invoke(); }
            catch (Exception ex) { Trace.WriteLine($"[CTP-SIM] ConnectionLost 订阅方异常（已忽略）：{ex.Message}"); }
        }
    }

    /// <summary>释放（幂等）。主动 Dispose 不发 ConnectionLost——会话层自己知道在关。</summary>
    public void Dispose()
    {
        lock (_teardownLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // 先置位再拆——接收循环的 finally 会走 TearDownWithEvent，不置位则主动关闭也会广播一次 ConnectionLost
        _lostRaised = 1;
        TearDown();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimRpcClient));
    }

    private readonly record struct Pending(TaskCompletionSource<JsonElement?> Tcs);
}
