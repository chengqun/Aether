using System.Runtime.InteropServices;
using System.Text;
using Ctp.Net.Core.Native;

namespace Ctp.Net.Core.Trader;

/// <summary>CTP 交易接口封装。生命周期：Create → LoginAsync → (查询/下单) → Dispose。</summary>
public sealed class CtpTrader : IDisposable
{
    // 注册 GB18030 编码提供程序（Mac/Linux 默认不含），用于解码 CTP 的 GBK 中文报文。
    static CtpTrader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // 真控制台流（static ctor 捕获 = 首个 CtpTrader 创建时，早于任何引擎重定向）：Lean 实盘引擎的
    // LiveTradingResultHandler.SetAlgorithm 会 Console.SetOut(algorithm.Debug)——本类回调线程的高频
    // 诊断打印若走 Console.WriteLine 会被喂进算法消息管道（有界/限流）⇒ 回调线程在管道满时阻塞 ⇒
    // SDK 派发全停 ⇒ 未应答队列堆满 rc=-2 ⇒ 4097 重连循环（2026-10-14 双 bot 浸泡实测 ~4:20 周期性
    // 死亡——每次会话 ~110 单 + ~80 查询后准时断）。写 OpenStandardOutput 真流：绕开重定向、线程安全
    // （Synchronized 包装），桌面 GUI 无控制台时与旧行为同（写到无人看的 stdout，无副作用）。
    private static readonly TextWriter RealOut =
        TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });


    private static string DecodeMsg(byte[]? bytes) =>
        bytes is null ? string.Empty :
        Encoding.GetEncoding("GB18030").GetString(bytes).TrimEnd('\0').TrimEnd();

    private IntPtr _handle;
    private GCHandle _selfHandle;
    private int _nextRequestId;

    // 前置连接一次性等待
    private readonly TaskCompletionSource<bool> _frontConnectedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 按 requestId 索引的待响应请求（认证/登录/查询）
    private readonly Dictionary<int, TaskCompletionSource<RspCallback>> _pending = new();
    private readonly object _pendingLock = new();

    // 持有委托引用，防止 GC 回收导致 CTP 回调时函数指针失效
    private readonly CtpTraderCallbacks.OnFrontConnectedDelegate _dFrontConnected;
    private readonly CtpTraderCallbacks.OnFrontDisconnectedDelegate _dFrontDisconnected;
    private readonly CtpTraderCallbacks.OnRspErrorDelegate _dRspError;
    private readonly CtpTraderCallbacks.OnRspAuthenticateDelegate _dRspAuthenticate;
    private readonly CtpTraderCallbacks.OnRspUserLoginDelegate _dRspUserLogin;
    private readonly CtpTraderCallbacks.OnRspQryTradingAccountDelegate _dQryAccount;
    private readonly CtpTraderCallbacks.OnRspQryInvestorPositionDelegate _dQryPosition;
    private readonly CtpTraderCallbacks.OnRspQryOrderDelegate _dQryOrder;


    private readonly CtpTraderCallbacks.OnRspQryInstrumentDelegate _dQryInstrument;
    private readonly CtpTraderCallbacks.OnRspOrderInsertDelegate _dRspOrderInsert;
    private readonly CtpTraderCallbacks.OnErrRtnOrderInsertDelegate _dErrRtnOrderInsert;
    private readonly CtpTraderCallbacks.OnRspOrderActionDelegate _dRspOrderAction;
    private readonly CtpTraderCallbacks.OnRtnOrderDelegate _dRtnOrder;
    private readonly CtpTraderCallbacks.OnRtnTradeDelegate _dRtnTrade;
    private readonly CtpTraderCallbacks.OnRspSettlementInfoConfirmDelegate _dRspSettlementInfoConfirm;

    // 查询类请求的逐项收集器（按 requestId 索引，isLast 起静默期收尾——见 OnQueryItem 注释）
    private readonly Dictionary<int, QueryCollector> _queryPending = new();

    /// <summary>多行收集的静默期（ms）：最后一次 isLast=true 后无新行才完成查询（本地前置 tag 帧恒 isLast=true）。</summary>
    private const int QueryQuietMs = 150;
    private int _nextOrderRef;   // 登录后由 MaxOrderRef 初始化

    public event EventHandler? FrontConnected;
    public event EventHandler<int>? FrontDisconnected;
    public event EventHandler<RspErrorEventArgs>? RspError;
    public event EventHandler<Order>? OrderReceived;
    public event EventHandler<Trade>? TradeReceived;

    public bool IsDisposed => _handle == IntPtr.Zero;

    public CtpTrader(string flowPath)
    {
        _selfHandle = GCHandle.Alloc(this);
        _dFrontConnected = OnFrontConnectedCb;
        _dFrontDisconnected = OnFrontDisconnectedCb;
        _dRspError = OnRspErrorCb;
        _dRspAuthenticate = OnRspAuthenticateCb;
        _dRspUserLogin = OnRspUserLoginCb;
        _dQryAccount = OnRspQryTradingAccountCb;
        _dQryPosition = OnRspQryInvestorPositionCb;
        _dQryOrder = OnRspQryOrderCb;
        _dQryInstrument = OnRspQryInstrumentCb;
        _dRspOrderInsert = OnRspOrderInsertCb;
        _dErrRtnOrderInsert = OnErrRtnOrderInsertCb;
        _dRspOrderAction = OnRspOrderActionCb;
        _dRtnOrder = OnRtnOrderCb;
        _dRtnTrade = OnRtnTradeCb;
        _dRspSettlementInfoConfirm = OnRspSettlementInfoConfirmCb;

        var cb = new CtpTraderCallbacks
        {
            OnFrontConnected = Marshal.GetFunctionPointerForDelegate(_dFrontConnected),
            OnFrontDisconnected = Marshal.GetFunctionPointerForDelegate(_dFrontDisconnected),
            OnRspError = Marshal.GetFunctionPointerForDelegate(_dRspError),
            OnRspAuthenticate = Marshal.GetFunctionPointerForDelegate(_dRspAuthenticate),
            OnRspUserLogin = Marshal.GetFunctionPointerForDelegate(_dRspUserLogin),
            OnRspQryTradingAccount = Marshal.GetFunctionPointerForDelegate(_dQryAccount),
            OnRspQryInvestorPosition = Marshal.GetFunctionPointerForDelegate(_dQryPosition),
            OnRspQryOrder = Marshal.GetFunctionPointerForDelegate(_dQryOrder),
            OnRspQryInstrument = Marshal.GetFunctionPointerForDelegate(_dQryInstrument),
            OnRspOrderInsert = Marshal.GetFunctionPointerForDelegate(_dRspOrderInsert),
            OnErrRtnOrderInsert = Marshal.GetFunctionPointerForDelegate(_dErrRtnOrderInsert),
            OnRspOrderAction = Marshal.GetFunctionPointerForDelegate(_dRspOrderAction),
            OnRtnOrder = Marshal.GetFunctionPointerForDelegate(_dRtnOrder),
            OnRtnTrade = Marshal.GetFunctionPointerForDelegate(_dRtnTrade),
            OnRspSettlementInfoConfirm = Marshal.GetFunctionPointerForDelegate(_dRspSettlementInfoConfirm),
        };
        _handle = NativeMethods.CtpTrader_Create(flowPath ?? "", ref cb, GCHandle.ToIntPtr(_selfHandle));
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CtpTrader_Create 失败");
    }

    // 查询族请求计数（⑰⑮查报单查合约——2026-10-15 定案：SDK 的链路计数在第 256 个**同族**请求起
    // 换 E2 逐字节标签编码形态，本地前置的应答回显无法满足客户端校验（五种候选形态全被拒、真前置
    // 无凭据不可取证）——宿主经 QueryRequestCount 在逼近边界前主动快速重连规避（计数清零）。
    private int _queryRequestCount;
    public int QueryRequestCount => _queryRequestCount;
    private void CountQuery() => Interlocked.Increment(ref _queryRequestCount);

    // reqId 序列跳过 tag 字母表 {E0..EF, FE}（2026-10-14 4:20 死循环根因的客户端侧防线）：
    // reqId 经 SDK 序列化上 wire，值落在字母表时 SDK 写 [E0][值] 转义；前置应答的 reqId 回显槽是
    // **定宽 1B**（实测 18B 转义形式同样被 SDK 丢弃不派发）⇒ 应答无法承载字母表值 ⇒ 查询挂死
    // rc=-2 ⇒ 4097 周期断连。reqId 只是对关联令牌（唯一即可、跳号无害）——在客户端源头永不产生
    // 字母表值：1..0xDF → 0xF0..0xFD → 回卷 1。
    // **回卷跳过 in-flight id**（2026-10-17 本地长跑定案）：跳字母表后可用空间仅 234 个号，做市商级
    // 高频会话（每 tick 撤/挂/查询）60-120s 耗尽回卷——旧「回卷时 pending 早已完成」的假设在高频下
    // 不成立（15s 超时的撤单/查询 pending 仍在等）⇒ 迟到应答错配给复用同 id 的新请求 ⇒ 查询挂死
    // rc=-2 ⇒ ~70s 周期 4097 断连（实测 MM 9 分钟 8 次）。跳过仍在两张 pending 表中的 id——分配点
    // 串行化（lock 内自增），空间余量 = 234 - in-flight（15s 超时封顶 ⇒ 请求速率 <15/s 时恒有余量）。
    private int NextRequestId()
    {
        while (true)
        {
            int n;
            lock (_pendingLock)
            {
                n = ++_nextRequestId;
                var b = n & 0xFF;
                if (b is (>= 0xE0 and <= 0xEF) or 0xFE) continue;   // 跳过字母表字节
                if (n >= 0xFD) _nextRequestId = 0;                    // 回卷（下一轮从 1 起）
                if (_pending.ContainsKey(n) || _queryPending.ContainsKey(n)) continue;   // 跳过 in-flight
            }
            return n;
        }
    }

    /// <summary>完整登录流程：连接前置 → 认证 → 登录。</summary>
    public async Task<LoginResult> LoginAsync(CtpConnectRequest req, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // 订阅私有/公共流（THOST_TERT_QUICK = 2），须在 Init 前
        NativeMethods.CtpTrader_SubscribePrivateTopic(_handle, 2);
        NativeMethods.CtpTrader_SubscribePublicTopic(_handle, 2);
        NativeMethods.CtpTrader_RegisterFront(_handle, req.FrontAddress);
        NativeMethods.CtpTrader_Init(_handle);

        // 1. 等前置连接
        await _frontConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        // 2. 认证
        var authReqId = NextRequestId();
        var authTcs = RegisterPending(authReqId);
        int rc = NativeMethods.CtpTrader_Authenticate(
            _handle, req.BrokerId, req.UserId, req.AppId, req.AuthCode, authReqId);
        if (rc != 0) throw new CtpException($"ReqAuthenticate 发送失败 rc={rc}");
        var authRsp = await authTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (authRsp.Info.ErrorID != 0)
            throw new CtpBusinessException(authRsp.Info.ErrorID, DecodeMsg(authRsp.Info.ErrorMsg));

        // 3. 登录（Mac 内部走 4 参 + 采集信息，C# 无感）
        var loginReqId = NextRequestId();
        var loginTcs = RegisterPending(loginReqId);
        rc = NativeMethods.CtpTrader_Login(
            _handle, req.BrokerId, req.UserId, req.Password, "", loginReqId);
        if (rc != 0) throw new CtpException($"ReqUserLogin 发送失败 rc={rc}");
        var loginRsp = await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (loginRsp.Info.ErrorID != 0)
            throw new CtpBusinessException(loginRsp.Info.ErrorID, DecodeMsg(loginRsp.Info.ErrorMsg));

        var f = (RspUserLoginField)loginRsp.Field!;
        if (int.TryParse(f.MaxOrderRef, out var mref)) _nextOrderRef = mref;

        // 确认结算结果（每个交易日下单前必须；登录后自动确认，使后续下单可用。失败不影响登录）
        try { await ConfirmSettlementAsync(req.BrokerId, req.UserId, ct); }
        catch (Exception ex) { RealOut.WriteLine($"[ctp-trader] 结算确认失败（不影响登录）: {ex.Message}"); }

        return new LoginResult(f.TradingDay, f.LoginTime, f.FrontID, f.SessionID, f.MaxOrderRef);
    }

    /// <summary>确认结算结果。每个交易日下单前必须调用（LoginAsync 已自动调）。</summary>
    public async Task ConfirmSettlementAsync(string brokerId, string investorId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        var tcs = RegisterPending(reqId);
        int rc = NativeMethods.CtpTrader_SettlementInfoConfirm(_handle, brokerId, investorId, reqId);
        if (rc != 0) throw new CtpException($"ReqSettlementInfoConfirm 发送失败 rc={rc}");
        var rsp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (rsp.Info.ErrorID != 0)
            throw new CtpBusinessException(rsp.Info.ErrorID, DecodeMsg(rsp.Info.ErrorMsg));
    }

    private string NextOrderRef()
    {
        int n = Interlocked.Increment(ref _nextOrderRef);
        return n.ToString("D12");   // CTP OrderRef 零填充（12 位够用）
    }

    // —— 查询 / 下单 / 撤单 ——

    public async Task<Account> QueryTradingAccountAsync(string brokerId, string accountId,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        CountQuery();
        var tcs = RegisterQuery(reqId);
        int rc = NativeMethods.CtpTrader_QryTradingAccount(_handle, brokerId, accountId, reqId);
        if (rc != 0) throw new CtpException($"ReqQryTradingAccount 发送失败 rc={rc}");
        var items = await tcs.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return items.Count > 0 ? Account.From((CtpAccountInfo)items[0]) : new Account();
    }

    public async Task<IReadOnlyList<Position>> QueryInvestorPositionAsync(
        string brokerId, string investorId, string? instrumentId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        CountQuery();
        var tcs = RegisterQuery(reqId);
        int rc = NativeMethods.CtpTrader_QryInvestorPosition(
            _handle, brokerId, investorId, instrumentId ?? "", reqId);
        if (rc != 0) throw new CtpException($"ReqQryInvestorPosition 发送失败 rc={rc}");
        var items = await tcs.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return items.Select(o => Position.From((CtpPositionInfo)o)).ToList();
    }

    /// <summary>查合约（instrumentId 过滤——null/空 = 全量；真实前置全量返回全市场清单，可能很大）。</summary>
    public async Task<IReadOnlyList<InstrumentInfo>> QueryInstrumentsAsync(string? instrumentId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        CountQuery();
        var tcs = RegisterQuery(reqId);
        int rc = NativeMethods.CtpTrader_QryInstrument(_handle, instrumentId ?? "", reqId);
        if (rc != 0) throw new CtpException($"ReqQryInstrument 发送失败 rc={rc}");
        var items = await tcs.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return items.Select(o => InstrumentInfo.From((CtpInstrumentInfo)o)).ToList();
    }

    public Task<OrderInsertResult> OrderInsertAsync(OrderRequest req, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var orderRef = NextOrderRef();
        var reqId = NextRequestId();
        var pod = new CtpOrderInsertRequest
        {
            BrokerID = req.BrokerId,
            InvestorID = req.InvestorId,
            InstrumentID = req.InstrumentId,
            OrderRef = orderRef,
            Direction = (byte)req.Direction,
            OffsetFlag = (byte)req.OffsetFlag,
            OrderPriceType = (byte)req.PriceType,
            LimitPrice = req.LimitPrice,
            VolumeTotalOriginal = req.Volume,
            TimeCondition = (byte)req.TimeCondition,
            ExchangeID = req.ExchangeId ?? "",
        };
        int rc = NativeMethods.CtpTrader_OrderInsert(_handle, ref pod, reqId);
        if (rc != 0) throw new CtpException($"ReqOrderInsert 发送失败 rc={rc}");
        // CTP 下单异步：报单状态经 OnRtnOrder 事件回报（OrderReceived）。
        // OnRspOrderInsert 仅在前端校验失败时触发，成功时不触发——故不阻塞等待，发送即返回。
        return Task.FromResult(new OrderInsertResult(orderRef, reqId));
    }

    public async Task OrderActionAsync(string brokerId, string investorId,
        int frontId, int sessionId, string orderRef, string exchangeId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        var tcs = RegisterPending(reqId);
        int rc = NativeMethods.CtpTrader_OrderAction(
            _handle, brokerId, investorId, frontId, sessionId, orderRef, exchangeId, reqId);
        if (rc != 0) throw new CtpException($"ReqOrderAction 发送失败 rc={rc}");
        var rsp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (rsp.Info.ErrorID != 0)
            throw new CtpBusinessException(rsp.Info.ErrorID, DecodeMsg(rsp.Info.ErrorMsg));
    }

    public async Task<IReadOnlyList<Order>> QueryOrderAsync(string brokerId, string investorId,
        string? instrumentId = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var reqId = NextRequestId();
        CountQuery();
        var tcs = RegisterQuery(reqId);
        int rc = NativeMethods.CtpTrader_QryOrder(
            _handle, brokerId, investorId, instrumentId ?? "", reqId);
        if (rc != 0) throw new CtpException($"ReqQryOrder 发送失败 rc={rc}");
        var items = await tcs.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return items.Select(o => Order.From((CtpOrderInfo)o)).ToList();
    }

    private QueryCollector RegisterQuery(int reqId)
    {
        var c = new QueryCollector();
        lock (_pendingLock) _queryPending[reqId] = c;
        return c;
    }

    private TaskCompletionSource<RspCallback> RegisterPending(int reqId)
    {
        var tcs = new TaskCompletionSource<RspCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[reqId] = tcs;
        return tcs;
    }

    private void TryCompletePending(int reqId, RspCallback cb)
    {
        TaskCompletionSource<RspCallback>? tcs;
        lock (_pendingLock) _pending.Remove(reqId, out tcs);
        tcs?.TrySetResult(cb);
    }

    // —— 回调（CTP 内部线程触发；结构体在此立即 marshal 成托管副本）——

    private static CtpTrader Self(IntPtr ctx) => (CtpTrader)GCHandle.FromIntPtr(ctx).Target!;

    private void OnFrontConnectedCb(IntPtr ctx)
    {
        var self = Self(ctx);
        RealOut.WriteLine("[ctp-trader] 前置已连接");
        self._frontConnectedTcs.TrySetResult(true);
        self.FrontConnected?.Invoke(self, EventArgs.Empty);
    }

    private void OnFrontDisconnectedCb(IntPtr ctx, int reason)
    {
        var self = Self(ctx);
        RealOut.WriteLine($"[ctp-trader] 前置断开 reason={reason}");
        self.FrontDisconnected?.Invoke(self, reason);
    }

    private void OnRspErrorCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-trader] RspError ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.RspError?.Invoke(self, new RspErrorEventArgs(i.ErrorID, DecodeMsg(i.ErrorMsg), requestId));
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnRspAuthenticateCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<RspAuthenticateField>(field) : default;
        RealOut.WriteLine($"[ctp-trader] 认证响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} AppID={f.AppID}");
        self.TryCompletePending(requestId, new RspCallback(i, f));
    }

    private void OnRspUserLoginCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<RspUserLoginField>(field) : default;
        RealOut.WriteLine($"[ctp-trader] 登录响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} TradingDay={f.TradingDay} FrontID={f.FrontID} SessionID={f.SessionID} MaxOrderRef={f.MaxOrderRef}");
        self.TryCompletePending(requestId, new RspCallback(i, f));
    }

    private void OnRspQryTradingAccountCb(IntPtr ctx, IntPtr account, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var a = account != IntPtr.Zero ? Marshal.PtrToStructure<CtpAccountInfo>(account) : default;
        RealOut.WriteLine($"[ctp-trader] 资金查询响应 ErrorID={i.ErrorID} isLast={isLast} Account={a.AccountID} Available={a.Available}");
        self.OnQueryItem(requestId, a, i, isLast);
    }

    private void OnRspQryInvestorPositionCb(IntPtr ctx, IntPtr position, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var p = position != IntPtr.Zero ? Marshal.PtrToStructure<CtpPositionInfo>(position) : default;
        RealOut.WriteLine($"[ctp-trader] 持仓查询响应 ErrorID={i.ErrorID} isLast={isLast} Instr={p.InstrumentID} Pos={p.Position}");
        self.OnQueryItem(requestId, p, i, isLast);
    }

    private void OnRspQryOrderCb(IntPtr ctx, IntPtr order, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var o = order != IntPtr.Zero ? Marshal.PtrToStructure<CtpOrderInfo>(order) : default;
        RealOut.WriteLine($"[ctp-trader] 报单查询响应 ErrorID={i.ErrorID} isLast={isLast} Instr={o.InstrumentID}");
        self.OnQueryItem(requestId, o, i, isLast);
    }

    private void OnRspQryInstrumentCb(IntPtr ctx, IntPtr instrument, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = instrument != IntPtr.Zero ? Marshal.PtrToStructure<CtpInstrumentInfo>(instrument) : default;
        RealOut.WriteLine($"[ctp-trader] 合约查询响应 ErrorID={i.ErrorID} isLast={isLast} Instr={f.InstrumentID}");
        self.OnQueryItem(requestId, f, i, isLast);
    }

    private void OnRspOrderInsertCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-trader] 下单响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnErrRtnOrderInsertCb(IntPtr ctx, IntPtr info)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-trader] 下单错误回报 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)}");
        self.RspError?.Invoke(self, new RspErrorEventArgs(i.ErrorID, DecodeMsg(i.ErrorMsg), 0));
    }

    private void OnRspOrderActionCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-trader] 撤单响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnRtnOrderCb(IntPtr ctx, IntPtr order)
    {
        var self = Self(ctx);
        if (order == IntPtr.Zero) return;
        var o = Marshal.PtrToStructure<CtpOrderInfo>(order);
        var ord = Order.From(o);
        RealOut.WriteLine($"[ctp-trader] 报单回报 {ord}");
        self.OrderReceived?.Invoke(self, ord);
    }

    private void OnRtnTradeCb(IntPtr ctx, IntPtr trade)
    {
        var self = Self(ctx);
        if (trade == IntPtr.Zero) return;
        var t = Marshal.PtrToStructure<CtpTradeInfo>(trade);
        var trd = Trade.From(t);
        RealOut.WriteLine($"[ctp-trader] 成交回报 {trd}");
        self.TradeReceived?.Invoke(self, trd);
    }

    private void OnRspSettlementInfoConfirmCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-trader] 结算确认响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnQueryItem(int reqId, object item, RspInfoField info, bool isLast)
    {
        QueryCollector? c;
        lock (_pendingLock) _queryPending.TryGetValue(reqId, out c);
        if (c is null) return;
        if (info.ErrorID != 0)
        {
            lock (_pendingLock) { _queryPending.Remove(reqId); c.CompletionTimer?.Dispose(); c.CompletionTimer = null; }
            c.Tcs.TrySetException(new CtpBusinessException(info.ErrorID, DecodeMsg(info.ErrorMsg)));
            return;
        }
        // 多行收集（2026-10-05 探针定案）：本地前置的多行 tag 应答**每帧都是完整响应**（isLast 恒 true——
        // tag 协议无 isLast=false 编码；真前置的多行走二进制列式路由，未逆向）。首个 isLast=true 立即完成会把
        // 同一写里后续帧的行丢掉（探针基线实证：SDK 两帧回调都发，旧版只收第一行）——改为<b>静默期收尾</b>：
        // 最后一次 isLast=true 后 150ms 无新行才完成，期间到达的行全部入列。真实前置的 false..true 序列
        // 同样兼容（false 只入列不起表，true 起表/续表），代价仅为每查询 +150ms。
        lock (_pendingLock) c.Items.Add(item);
        if (isLast)
        {
            lock (_pendingLock)
            {
                if (c.CompletionTimer is null)
                    c.CompletionTimer = new Timer(_ => CompleteQuery(reqId, c), null, QueryQuietMs, Timeout.Infinite);
                else
                    c.CompletionTimer.Change(QueryQuietMs, Timeout.Infinite);   // 新行到达——重置静默期
            }
        }
    }

    /// <summary>静默期到点收尾（timer 线程）：该 reqId 仍是本收集器才完成（防御 reqId 复用/竞态）。</summary>
    private void CompleteQuery(int reqId, QueryCollector c)
    {
        lock (_pendingLock)
        {
            if (!_queryPending.TryGetValue(reqId, out var cur) || !ReferenceEquals(cur, c)) return;
            _queryPending.Remove(reqId);
            c.CompletionTimer?.Dispose();
            c.CompletionTimer = null;
            c.Tcs.TrySetResult(c.Items);
        }
    }

    private sealed class QueryCollector
    {
        public readonly List<object> Items = new();
        public readonly TaskCompletionSource<IReadOnlyList<object>> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>静默期收尾计时器（首个 isLast=true 创建，后续行 Change 续表）——见 OnQueryItem 注释。</summary>
        public Timer? CompletionTimer;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        // Release 不得在 CTP 回调线程内调用；此处为调用方线程，安全。
        NativeMethods.CtpTrader_Release(_handle);
        _handle = IntPtr.Zero;
        _selfHandle.Free();
    }

    /// <summary>回调结果：已 marshal 的托管副本（回调返回后 CTP 原指针即失效）。</summary>
    private readonly struct RspCallback(RspInfoField info, object? field)
    {
        public readonly RspInfoField Info = info;
        public readonly object? Field = field;
    }
}
