using System.Runtime.InteropServices;

namespace Ctp.Net.Core.Native;

// CTP 6.7.2 结构体 C# 镜像 —— 严格按 6.7.2 头文件布局（三平台一致，可直传）。
// 仅镜像 Phase 1 登录流程所需的结构体；后续阶段按需补充。

/// <summary>响应信息（错误码 + 错误消息）。ErrorMsg 为 GBK 字节，需用 GB18030 解码。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RspInfoField
{
    public int ErrorID;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 81)]
    public byte[] ErrorMsg;
}

/// <summary>认证响应。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct RspAuthenticateField
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 11)]
    public string BrokerID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string UserID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 11)]
    public string UserProductInfo;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string AppID;
    public byte AppType; // TThostFtdcAppTypeType = char
}

/// <summary>登录响应（含 FrontID/SessionID/MaxOrderRef，订单回报匹配键）。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct RspUserLoginField
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string TradingDay;   // TThostFtdcDateType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string LoginTime;    // TThostFtdcTimeType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 11)]
    public string BrokerID;     // TThostFtdcBrokerIDType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string UserID;       // TThostFtdcUserIDType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)]
    public string SystemName;   // TThostFtdcSystemNameType
    public int FrontID;         // TThostFtdcFrontIDType
    public int SessionID;       // TThostFtdcSessionIDType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)]
    public string MaxOrderRef;  // TThostFtdcOrderRefType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string SHFETime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string DCETime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string CZCETime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string FFEXTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string INETime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)]
    public string SysVersion;   // TThostFtdcSysVersionType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string GFEXTime;
}

/// <summary>订阅/退订行情的合约标识。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SpecificInstrumentField
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)]
    public string Reserve1;   // TThostFtdcOldInstrumentIDType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)]
    public string InstrumentID;
}

/// <summary>深度行情（Tick）。字段顺序严格按 6.7.2 头文件。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DepthMarketDataField
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string TradingDay;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)]
    public string Reserve1;          // OldInstrumentIDType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string ExchangeID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)]
    public string Reserve2;          // OldExchangeInstIDType
    public double LastPrice;
    public double PreSettlementPrice;
    public double PreClosePrice;
    public double PreOpenInterest;   // LargeVolumeType = double
    public double OpenPrice;
    public double HighestPrice;
    public double LowestPrice;
    public int Volume;               // VolumeType = int
    public double Turnover;          // MoneyType
    public double OpenInterest;      // LargeVolumeType
    public double ClosePrice;
    public double SettlementPrice;
    public double UpperLimitPrice;
    public double LowerLimitPrice;
    public double PreDelta;          // RatioType
    public double CurrDelta;         // RatioType
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string UpdateTime;
    public int UpdateMillisec;
    public double BidPrice1; public int BidVolume1;
    public double AskPrice1; public int AskVolume1;
    public double BidPrice2; public int BidVolume2;
    public double AskPrice2; public int AskVolume2;
    public double BidPrice3; public int BidVolume3;
    public double AskPrice3; public int AskVolume3;
    public double BidPrice4; public int BidVolume4;
    public double AskPrice4; public int AskVolume4;
    public double BidPrice5; public int BidVolume5;
    public double AskPrice5; public int AskVolume5;
    public double AveragePrice;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
    public string ActionDay;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)]
    public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)]
    public string ExchangeInstID;
    public double BandingUpperPrice;
    public double BandingLowerPrice;
}

// —— wrapper 定义的稳定 POD 镜像（C# 侧按此布局 marshal）——
// 注意：CTP 的 char 枚举字段是 1 字节，C# 用 byte（不可用 char，.NET char 是 2 字节）。

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CtpAccountInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 11)] public string BrokerID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)] public string AccountID;
    public double Balance;
    public double Available;
    public double WithdrawQuota;
    public double FrozenMargin;
    public double FrozenCash;
    public double FrozenCommission;
    public double Commission;
    public double CurrMargin;
    public double PositionProfit;
    public double CloseProfit;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string TradingDay;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CtpPositionInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string ExchangeID;
    public byte PosiDirection;
    public byte HedgeFlag;
    public byte PositionDate;
    public int YdPosition;
    public int Position;
    public int LongFrozen;
    public int ShortFrozen;
    public int TodayPosition;
    public double PositionCost;
    public double UseMargin;
    public double PositionProfit;
    public double CloseProfit;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CtpInstrumentInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string ExchangeID;
    public double VolumeMultiple;
    public double PriceTick;
    public double PreSettlement;
}

public struct CtpOrderInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string ExchangeID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)] public string OrderRef;
    public int FrontID;
    public int SessionID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string OrderSysID;
    public byte Direction;
    public byte OffsetFlag;
    public byte OrderPriceType;
    public double LimitPrice;
    public int VolumeTotalOriginal;
    public int VolumeTraded;
    public int VolumeTotal;
    public byte OrderStatus;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string InsertTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string UpdateTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 81)] public byte[] StatusMsg;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CtpTradeInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)] public string OrderRef;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string TradeID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string OrderSysID;
    public byte Direction;
    public byte OffsetFlag;
    public byte HedgeFlag;
    public double Price;
    public int Volume;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string TradeTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string TradingDay;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CtpOrderInsertRequest
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 11)] public string BrokerID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)] public string InvestorID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string InstrumentID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)] public string OrderRef;
    public byte Direction;       // '0' Buy / '1' Sell
    public byte OffsetFlag;      // '0' Open / '1' Close / '3' CloseToday
    public byte OrderPriceType;  // '2' Limit / '0' Market
    public double LimitPrice;
    public int VolumeTotalOriginal;
    public byte TimeCondition;   // '3' GFD / '1' IOC / '2' FOK
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)] public string ExchangeID;   // M2 抓包补：SimNow 等前置要求非空（尾部追加，与 ctp_api.h 同步；构造点显式赋值）
}
