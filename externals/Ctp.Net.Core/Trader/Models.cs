using Ctp.Net.Core.Native;

namespace Ctp.Net.Core.Trader;

/// <summary>登录连接参数。</summary>
public sealed class CtpConnectRequest
{
    public required string FrontAddress { get; init; } // tcp://ip:port
    public required string BrokerId { get; init; }
    public required string UserId { get; init; }
    public required string Password { get; init; }
    public required string AppId { get; init; }
    public required string AuthCode { get; init; }
}

/// <summary>登录结果：含订单回报匹配键 FrontID/SessionID/MaxOrderRef。</summary>
public sealed record LoginResult(
    string TradingDay, string LoginTime, int FrontID, int SessionID, string MaxOrderRef);

public sealed class RspErrorEventArgs(int errorId, string errorMsg, int requestId) : EventArgs
{
    public int ErrorId { get; } = errorId;
    public string ErrorMsg { get; } = errorMsg;
    public int RequestId { get; } = requestId;
}

public class CtpException(string message) : Exception(message);

public class CtpBusinessException(int errorId, string errorMsg)
    : CtpException($"CTP 业务错误 [{errorId}]: {errorMsg}")
{
    public int ErrorId { get; } = errorId;
    public string ErrorMsg { get; } = errorMsg;
}

// —— 交易枚举（byte 基，值 = CTP char 码）——
public enum Direction : byte { Buy = (byte)'0', Sell = (byte)'1' }
public enum OffsetFlag : byte { Open = (byte)'0', Close = (byte)'1', CloseToday = (byte)'3', CloseYesterday = (byte)'4' }
public enum OrderPriceType : byte { Market = (byte)'0', Limit = (byte)'2' }
public enum TimeCondition : byte { IOC = (byte)'1', FOK = (byte)'2', GFD = (byte)'3' }
public enum PosiDirection : byte { Net = (byte)'1', Long = (byte)'2', Short = (byte)'3' }
public enum HedgeFlag : byte { Speculation = (byte)'1', Arbitrage = (byte)'2', Hedge = (byte)'3' }
public enum PositionDate : byte { Today = (byte)'1', History = (byte)'2' }

// —— 下单请求 / 结果 ——
public sealed class OrderRequest
{
    public required string BrokerId { get; init; }
    public required string InvestorId { get; init; }
    public required string InstrumentId { get; init; }
    public required Direction Direction { get; init; }
    public required OffsetFlag OffsetFlag { get; init; }
    public required OrderPriceType PriceType { get; init; }
    public required double LimitPrice { get; init; }
    public required int Volume { get; init; }
    public TimeCondition TimeCondition { get; init; } = TimeCondition.GFD;
    /// <summary>交易所代码（SHFE/DCE/…）。留空 = 旧行为；SimNow 等前置对报单要求非空（M2 抓包实证）。</summary>
    public string ExchangeId { get; init; } = "";
}

public sealed record OrderInsertResult(string OrderRef, int RequestId);

// —— 查询/回报模型 ——

/// <summary>合约静态元数据（查合约应答行——对齐 Domain CtpInstrument 的字段面）。</summary>
public sealed record InstrumentInfo(string InstrumentId, string ExchangeId,
    double VolumeMultiple, double PriceTick, double PreSettlement)
{
    public static InstrumentInfo From(Native.CtpInstrumentInfo f) => new(
        (f.InstrumentID ?? "").Trim(), (f.ExchangeID ?? "").Trim(), f.VolumeMultiple, f.PriceTick, f.PreSettlement);
}
public sealed record Account
{
    public string AccountId { get; init; } = "";
    public double Balance { get; init; }
    public double Available { get; init; }
    public double WithdrawQuota { get; init; }
    public double FrozenMargin { get; init; }
    public double FrozenCash { get; init; }
    public double Commission { get; init; }
    public double CurrMargin { get; init; }
    public double PositionProfit { get; init; }
    public double CloseProfit { get; init; }

    public static Account From(CtpAccountInfo a) => new()
    {
        AccountId = a.AccountID,
        Balance = a.Balance, Available = a.Available, WithdrawQuota = a.WithdrawQuota,
        FrozenMargin = a.FrozenMargin, FrozenCash = a.FrozenCash, Commission = a.Commission,
        CurrMargin = a.CurrMargin, PositionProfit = a.PositionProfit, CloseProfit = a.CloseProfit,
    };
}

public sealed record Position
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public PosiDirection PosiDirection { get; init; }
    public HedgeFlag HedgeFlag { get; init; }
    public PositionDate PositionDate { get; init; }
    public int YdPosition { get; init; }
    public int TotalPosition { get; init; }
    public int TodayPosition { get; init; }
    public double UseMargin { get; init; }
    public double PositionProfit { get; init; }
    public double CloseProfit { get; init; }

    public static Position From(CtpPositionInfo p) => new()
    {
        InstrumentId = p.InstrumentID, ExchangeId = p.ExchangeID,
        PosiDirection = (PosiDirection)p.PosiDirection,
        HedgeFlag = (HedgeFlag)p.HedgeFlag,
        PositionDate = (PositionDate)p.PositionDate,
        YdPosition = p.YdPosition, TotalPosition = p.Position, TodayPosition = p.TodayPosition,
        UseMargin = p.UseMargin, PositionProfit = p.PositionProfit, CloseProfit = p.CloseProfit,
    };

    public override string ToString() =>
        $"{InstrumentId} {PosiDirection} pos={TotalPosition}(今{TodayPosition}/昨{YdPosition}) 盈亏={PositionProfit:F2}";
}

public sealed record Order
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public int FrontID { get; init; }
    public int SessionID { get; init; }
    public string OrderSysID { get; init; } = "";
    public Direction Direction { get; init; }
    public OffsetFlag OffsetFlag { get; init; }
    public OrderPriceType PriceType { get; init; }
    public double LimitPrice { get; init; }
    public int VolumeTotalOriginal { get; init; }
    public int VolumeTraded { get; init; }
    public int VolumeTotal { get; init; }
    public byte OrderStatusRaw { get; init; }
    public string InsertTime { get; init; } = "";
    public string UpdateTime { get; init; } = "";
    public string StatusMsg { get; init; } = "";

    public static Order From(CtpOrderInfo o) => new()
    {
        InstrumentId = o.InstrumentID, ExchangeId = o.ExchangeID, OrderRef = o.OrderRef,
        FrontID = o.FrontID, SessionID = o.SessionID, OrderSysID = o.OrderSysID,
        Direction = (Direction)o.Direction, OffsetFlag = (OffsetFlag)o.OffsetFlag,
        PriceType = (OrderPriceType)o.OrderPriceType, LimitPrice = o.LimitPrice,
        VolumeTotalOriginal = o.VolumeTotalOriginal, VolumeTraded = o.VolumeTraded,
        VolumeTotal = o.VolumeTotal, OrderStatusRaw = o.OrderStatus,
        InsertTime = o.InsertTime, UpdateTime = o.UpdateTime,
        StatusMsg = DecodeGbk(o.StatusMsg),
    };

    private static string DecodeGbk(byte[]? b) =>
        b is null ? "" : System.Text.Encoding.GetEncoding("GB18030").GetString(b).TrimEnd('\0').TrimEnd();

    public override string ToString() =>
        $"{InstrumentId} {Direction}{OffsetFlag} {VolumeTotalOriginal}@{LimitPrice} " +
        $"成{VolumeTraded} 状态={(char)OrderStatusRaw} {StatusMsg} ref={OrderRef}";
}

public sealed record Trade
{
    public string InstrumentId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public string TradeId { get; init; } = "";
    public string OrderSysID { get; init; } = "";
    public Direction Direction { get; init; }
    public OffsetFlag OffsetFlag { get; init; }
    public double Price { get; init; }
    public int Volume { get; init; }
    public string TradeTime { get; init; } = "";

    public static Trade From(CtpTradeInfo t) => new()
    {
        InstrumentId = t.InstrumentID, OrderRef = t.OrderRef,
        TradeId = t.TradeID, OrderSysID = t.OrderSysID,
        Direction = (Direction)t.Direction, OffsetFlag = (OffsetFlag)t.OffsetFlag,
        Price = t.Price, Volume = t.Volume, TradeTime = t.TradeTime,
    };

    public override string ToString() =>
        $"{InstrumentId} {Direction}{OffsetFlag} {Volume}@{Price} time={TradeTime} ref={OrderRef}";
}
