using System.Runtime.InteropServices;

namespace Ctp.Net.Core.Native;

// C ABI 回调函数指针表。委托实例须由 CtpTrader 持有引用以防 GC 回收（否则 CTP
// 回调时函数指针失效 → 崩溃）。bool 参数用 I1（C99 _Bool，1 字节）。
[StructLayout(LayoutKind.Sequential)]
public struct CtpTraderCallbacks
{
    public IntPtr OnFrontConnected;
    public IntPtr OnFrontDisconnected;
    public IntPtr OnRspError;
    public IntPtr OnRspAuthenticate;
    public IntPtr OnRspUserLogin;
    public IntPtr OnRspQryTradingAccount;
    public IntPtr OnRspQryInvestorPosition;
    public IntPtr OnRspQryOrder;
    public IntPtr OnRspQryInstrument;
    public IntPtr OnRspOrderInsert;
    public IntPtr OnErrRtnOrderInsert;
    public IntPtr OnRspOrderAction;
    public IntPtr OnRtnOrder;
    public IntPtr OnRtnTrade;
    public IntPtr OnRspSettlementInfoConfirm;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnFrontConnectedDelegate(IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnFrontDisconnectedDelegate(IntPtr ctx, int reason);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspErrorDelegate(IntPtr ctx, IntPtr info, int requestId,
        [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspAuthenticateDelegate(IntPtr ctx, IntPtr field, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspUserLoginDelegate(IntPtr ctx, IntPtr field, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspQryTradingAccountDelegate(IntPtr ctx, IntPtr account, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspQryInvestorPositionDelegate(IntPtr ctx, IntPtr position, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspOrderInsertDelegate(IntPtr ctx, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnErrRtnOrderInsertDelegate(IntPtr ctx, IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspOrderActionDelegate(IntPtr ctx, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspQryOrderDelegate(IntPtr ctx, IntPtr order, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspQryInstrumentDelegate(IntPtr ctx, IntPtr instrument, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRtnOrderDelegate(IntPtr ctx, IntPtr order);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRtnTradeDelegate(IntPtr ctx, IntPtr trade);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspSettlementInfoConfirmDelegate(IntPtr ctx, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);
}

// P/Invoke 绑定。库名 ctp_native → libctp_native.dylib / .so / .dll。
internal static class NativeMethods
{
    private const string Lib = "ctp_native";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr CtpTrader_Create(string flowPath,
        ref CtpTraderCallbacks callbacks, IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CtpTrader_Release(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpTrader_Join(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_RegisterFront(IntPtr handle, string front);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpTrader_SubscribePrivateTopic(IntPtr handle, int resumeType);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpTrader_SubscribePublicTopic(IntPtr handle, int resumeType);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpTrader_Init(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_Authenticate(IntPtr handle,
        string brokerId, string userId, string appId, string authCode, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_Login(IntPtr handle,
        string brokerId, string userId, string password, string userAgent, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_SetCollectionInfo(IntPtr handle,
        byte[] buf, int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_QryTradingAccount(IntPtr handle,
        string brokerId, string accountId, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_QryInvestorPosition(IntPtr handle,
        string brokerId, string investorId, string instrumentId, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpTrader_OrderInsert(IntPtr handle,
        ref CtpOrderInsertRequest req, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_OrderAction(IntPtr handle,
        string brokerId, string investorId,
        int frontId, int sessionId, string orderRef, string exchangeId, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_QryOrder(IntPtr handle,
        string brokerId, string investorId, string instrumentId, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_QryInstrument(IntPtr handle, string instrumentId, int requestId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpTrader_SettlementInfoConfirm(IntPtr handle,
        string brokerId, string investorId, int requestId);
}

// Md 回调函数指针表。委托须由 CtpMarketData 持有引用防 GC。
[StructLayout(LayoutKind.Sequential)]
public struct CtpMdCallbacks
{
    public IntPtr OnFrontConnected;
    public IntPtr OnFrontDisconnected;
    public IntPtr OnRspError;
    public IntPtr OnRspUserLogin;
    public IntPtr OnRspSubMarketData;
    public IntPtr OnRspUnSubMarketData;
    public IntPtr OnRtnDepthMarketData;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnFrontConnectedDelegate(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnFrontDisconnectedDelegate(IntPtr ctx, int reason);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspErrorDelegate(IntPtr ctx, IntPtr info, int requestId,
        [MarshalAs(UnmanagedType.I1)] bool isLast);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspUserLoginDelegate(IntPtr ctx, IntPtr field, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspSubMarketDataDelegate(IntPtr ctx, IntPtr field, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRspUnSubMarketDataDelegate(IntPtr ctx, IntPtr field, IntPtr info,
        int requestId, [MarshalAs(UnmanagedType.I1)] bool isLast);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnRtnDepthMarketDataDelegate(IntPtr ctx, IntPtr field);
}

internal static class MdNativeMethods
{
    private const string Lib = "ctp_native";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr CtpMd_Create(string flowPath,
        ref CtpMdCallbacks callbacks, IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CtpMd_Release(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpMd_Join(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpMd_RegisterFront(IntPtr handle, string front);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CtpMd_Init(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpMd_Login(IntPtr handle,
        string brokerId, string userId, string password, int requestId);

    // string[] 经 marshaller 转为 ANSI char*[] 数组，调用期间有效
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpMd_SubscribeMarketData(IntPtr handle,
        string[] symbols, int count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int CtpMd_UnSubscribeMarketData(IntPtr handle,
        string[] symbols, int count);
}
