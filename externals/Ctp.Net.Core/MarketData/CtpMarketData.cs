using System.Runtime.InteropServices;
using System.Text;
using Ctp.Net.Core.Native;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Core.MarketData;

/// <summary>CTP 行情接口封装。生命周期：Create → LoginAsync → SubscribeMarketDataAsync → (OnTick) → Dispose。</summary>
public sealed class CtpMarketData : IDisposable
{
    // 注册 GB18030 编码提供程序（解码 CTP 的 GBK 中文报文）。与 CtpTrader 共用，任一被使用即注册。
    static CtpMarketData()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private IntPtr _handle;
    private GCHandle _selfHandle;
    private int _nextRequestId;

    private readonly TaskCompletionSource<bool> _frontConnectedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RspCallback> _loginTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, TaskCompletionSource<RspCallback>> _pending = new();
    private readonly object _pendingLock = new();

    private readonly CtpMdCallbacks.OnFrontConnectedDelegate _dFrontConnected;
    private readonly CtpMdCallbacks.OnFrontDisconnectedDelegate _dFrontDisconnected;
    private readonly CtpMdCallbacks.OnRspErrorDelegate _dRspError;
    private readonly CtpMdCallbacks.OnRspUserLoginDelegate _dRspUserLogin;
    private readonly CtpMdCallbacks.OnRspSubMarketDataDelegate _dRspSub;
    private readonly CtpMdCallbacks.OnRspUnSubMarketDataDelegate _dUnSub;
    private readonly CtpMdCallbacks.OnRtnDepthMarketDataDelegate _dRtnDepth;

    public event EventHandler? FrontConnected;
    public event EventHandler<int>? FrontDisconnected;
    public event EventHandler<TickData>? TickReceived;

    /// <summary>订阅/退订应答（M3 E2E 用——SDK 即裁判的订阅链路断言面）。</summary>
    public event EventHandler<(string InstrumentId, int ErrorId, string ErrorMsg, bool IsLast)>? SubMarketDataResult;
    public event EventHandler<RspErrorEventArgs>? RspError;

    public bool IsDisposed => _handle == IntPtr.Zero;

    public CtpMarketData(string flowPath)
    {
        _selfHandle = GCHandle.Alloc(this);
        _dFrontConnected = OnFrontConnectedCb;
        _dFrontDisconnected = OnFrontDisconnectedCb;
        _dRspError = OnRspErrorCb;
        _dRspUserLogin = OnRspUserLoginCb;
        _dRspSub = OnRspSubMarketDataCb;
        _dUnSub = OnRspUnSubMarketDataCb;
        _dRtnDepth = OnRtnDepthMarketDataCb;

        var cb = new CtpMdCallbacks
        {
            OnFrontConnected = Marshal.GetFunctionPointerForDelegate(_dFrontConnected),
            OnFrontDisconnected = Marshal.GetFunctionPointerForDelegate(_dFrontDisconnected),
            OnRspError = Marshal.GetFunctionPointerForDelegate(_dRspError),
            OnRspUserLogin = Marshal.GetFunctionPointerForDelegate(_dRspUserLogin),
            OnRspSubMarketData = Marshal.GetFunctionPointerForDelegate(_dRspSub),
            OnRspUnSubMarketData = Marshal.GetFunctionPointerForDelegate(_dUnSub),
            OnRtnDepthMarketData = Marshal.GetFunctionPointerForDelegate(_dRtnDepth),
        };
        _handle = MdNativeMethods.CtpMd_Create(flowPath ?? "", ref cb, GCHandle.ToIntPtr(_selfHandle));
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CtpMd_Create 失败");
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>连接前置并登录行情。Md 登录不需认证/采集。</summary>
    public async Task LoginAsync(string front, string brokerId, string userId, string password,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        MdNativeMethods.CtpMd_RegisterFront(_handle, front);
        MdNativeMethods.CtpMd_Init(_handle);

        await _frontConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        var reqId = NextRequestId();
        int rc = MdNativeMethods.CtpMd_Login(_handle, brokerId, userId, password, reqId);
        if (rc != 0) throw new CtpException($"CtpMd_Login 发送失败 rc={rc}");
        var rsp = await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (rsp.Info.ErrorID != 0)
            throw new CtpBusinessException(rsp.Info.ErrorID, DecodeMsg(rsp.Info.ErrorMsg));
    }

    /// <summary>订阅行情。完成后开始接收 OnTick。</summary>
    public async Task SubscribeMarketDataAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var arr = symbols.ToArray();
        int rc = MdNativeMethods.CtpMd_SubscribeMarketData(_handle, arr, arr.Length);
        if (rc != 0) throw new CtpException($"SubscribeMarketData 发送失败 rc={rc}");
        // 不阻塞等待 OnRspSubMarketData（订阅结果按合约逐个回调；Tick 流即开始）
        await Task.CompletedTask;
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

    // —— 回调（CTP 内部线程）——

    private static CtpMarketData Self(IntPtr ctx) => (CtpMarketData)GCHandle.FromIntPtr(ctx).Target!;

    private void OnFrontConnectedCb(IntPtr ctx)
    {
        var self = Self(ctx);
        Console.WriteLine("[ctp-md] 前置已连接");
        self._frontConnectedTcs.TrySetResult(true);
        self.FrontConnected?.Invoke(self, EventArgs.Empty);
    }

    private void OnFrontDisconnectedCb(IntPtr ctx, int reason)
    {
        var self = Self(ctx);
        Console.WriteLine($"[ctp-md] 前置断开 reason={reason}");
        self.FrontDisconnected?.Invoke(self, reason);
    }

    private void OnRspErrorCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        Console.WriteLine($"[ctp-md] RspError ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.RspError?.Invoke(self, new RspErrorEventArgs(i.ErrorID, DecodeMsg(i.ErrorMsg), requestId));
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnRspUserLoginCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<RspUserLoginField>(field) : default;
        Console.WriteLine($"[ctp-md] 登录响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} TradingDay={f.TradingDay}");
        self._loginTcs.TrySetResult(new RspCallback(i, null));
    }

    private void OnRspSubMarketDataCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<SpecificInstrumentField>(field) : default;
        Console.WriteLine($"[ctp-md] 订阅响应 InstrumentID={f.InstrumentID} ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} isLast={isLast}");
        Self(ctx).SubMarketDataResult?.Invoke(Self(ctx), (f.InstrumentID, i.ErrorID, DecodeMsg(i.ErrorMsg), isLast));
    }

    private void OnRspUnSubMarketDataCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        Console.WriteLine($"[ctp-md] 退订响应 ErrorID={i.ErrorID} isLast={isLast}");
    }

    private void OnRtnDepthMarketDataCb(IntPtr ctx, IntPtr field)
    {
        var self = Self(ctx);
        if (field == IntPtr.Zero) return;
        var f = Marshal.PtrToStructure<DepthMarketDataField>(field);
        Console.WriteLine($"[ctp-md] Tick {f.InstrumentID} last={f.LastPrice} bid={f.BidPrice1}x{f.BidVolume1} ask={f.AskPrice1}x{f.AskVolume1} vol={f.Volume} {f.UpdateTime}.{f.UpdateMillisec}");
        self.TickReceived?.Invoke(self, TickData.From(f));
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        MdNativeMethods.CtpMd_Release(_handle);
        _handle = IntPtr.Zero;
        _selfHandle.Free();
    }

    private static string DecodeMsg(byte[]? b) =>
        b is null ? string.Empty :
        System.Text.Encoding.GetEncoding("GB18030").GetString(b).TrimEnd('\0').TrimEnd();

    private readonly struct RspCallback(RspInfoField info, object? field)
    {
        public readonly RspInfoField Info = info;
        public readonly object? Field = field;
    }
}
