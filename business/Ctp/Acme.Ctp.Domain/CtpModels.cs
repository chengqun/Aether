namespace Acme.Ctp.Domain;

// —— CTP 领域 DTO（纯数据载体，无行为；字段镜像 Ctp.Net.Core 模型，由 Infrastructure 侧映射）——
// 这些类型放在 Domain.Abstractions，使 ICtpSession 契约不依赖外部 SDK，Application/UI 层只面向领域 DTO。

/// <summary>CTP 登录连接参数（交易前置 + 行情前置 + 凭据）。</summary>
public sealed class CtpLoginRequest
{
    public required string FrontAddress { get; init; }   // 交易前置 tcp://ip:port
    public required string MdFrontAddress { get; init; } // 行情前置 tcp://ip:port
    public required string BrokerId { get; init; }
    public required string UserId { get; init; }
    public required string Password { get; init; }
    public required string AppId { get; init; }
    public required string AuthCode { get; init; }
}

/// <summary>登录结果：含订单回报匹配键 FrontID/SessionID/MaxOrderRef。</summary>
public sealed record CtpLoginResult(
    string TradingDay, string LoginTime, int FrontId, int SessionId, string MaxOrderRef);

/// <summary>资金账户。</summary>
public sealed record CtpAccount
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

    public override string ToString() =>
        $"账户 {AccountId} 权益={Balance:F2} 可用={Available:F2} 冻结保证金={FrozenMargin:F2} 持仓盈亏={PositionProfit:F2} 平仓盈亏={CloseProfit:F2}";
}

/// <summary>持仓。</summary>
public sealed record CtpPosition
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public CtpPosiDirection PosiDirection { get; init; }
    public CtpHedgeFlag HedgeFlag { get; init; }
    public CtpPositionDate PositionDate { get; init; }
    public int YdPosition { get; init; }
    public int TotalPosition { get; init; }
    public int TodayPosition { get; init; }
    public double UseMargin { get; init; }
    public double PositionProfit { get; init; }
    public double CloseProfit { get; init; }

    public override string ToString() =>
        $"{InstrumentId} {PosiDirection} pos={TotalPosition}(今{TodayPosition}/昨{YdPosition}) 盈亏={PositionProfit:F2}";
}

/// <summary>报单（订单）。</summary>
public sealed record CtpOrder
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public int FrontId { get; init; }
    public int SessionId { get; init; }
    public string OrderSysId { get; init; } = "";
    public CtpDirection Direction { get; init; }
    public CtpOffsetFlag OffsetFlag { get; init; }
    public CtpOrderPriceType PriceType { get; init; }
    public double LimitPrice { get; init; }
    public int VolumeTotalOriginal { get; init; }
    public int VolumeTraded { get; init; }
    public int VolumeTotal { get; init; }
    public byte OrderStatusRaw { get; init; }
    public string InsertTime { get; init; } = "";
    public string UpdateTime { get; init; } = "";
    public string StatusMsg { get; init; } = "";

    public override string ToString() =>
        $"{InstrumentId} {Direction}{OffsetFlag} {VolumeTotalOriginal}@{LimitPrice} " +
        $"成{VolumeTraded} 状态={(char)OrderStatusRaw} {StatusMsg} ref={OrderRef}";
}

/// <summary>成交。</summary>
public sealed record CtpTrade
{
    public string InstrumentId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public string TradeId { get; init; } = "";
    public string OrderSysId { get; init; } = "";
    public CtpDirection Direction { get; init; }
    public CtpOffsetFlag OffsetFlag { get; init; }
    public double Price { get; init; }
    public int Volume { get; init; }
    public string TradeTime { get; init; } = "";

    public override string ToString() =>
        $"{InstrumentId} {Direction}{OffsetFlag} {Volume}@{Price} time={TradeTime} ref={OrderRef}";
}

/// <summary>Tick 行情。</summary>
public sealed record CtpTick
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string TradingDay { get; init; } = "";
    public string ActionDay { get; init; } = "";
    public string UpdateTime { get; init; } = "";       // HH:mm:ss
    public int UpdateMillisec { get; init; }
    public double LastPrice { get; init; }
    public long Volume { get; init; }
    public double OpenInterest { get; init; }
    public double Turnover { get; init; }
    public double BidPrice { get; init; }
    public int BidVolume { get; init; }
    public double AskPrice { get; init; }
    public int AskVolume { get; init; }
    public double PreSettlement { get; init; }
    public double PreClose { get; init; }
    public double OpenPrice { get; init; }
    public double Highest { get; init; }
    public double Lowest { get; init; }
}

/// <summary>下单请求。</summary>
public sealed class CtpOrderRequest
{
    public required string BrokerId { get; init; }
    public required string InvestorId { get; init; }
    public required string InstrumentId { get; init; }
    public required CtpDirection Direction { get; init; }
    public required CtpOffsetFlag OffsetFlag { get; init; }
    public required CtpOrderPriceType PriceType { get; init; }
    public required double LimitPrice { get; init; }
    public required int Volume { get; init; }
    public CtpTimeCondition TimeCondition { get; init; } = CtpTimeCondition.GFD;
}

/// <summary>下单结果。</summary>
public sealed record CtpOrderInsertResult(string OrderRef, int RequestId);

/// <summary>撤单定位键（FrontId/SessionId/OrderRef 唯一定位一笔报单）。</summary>
public sealed record CtpOrderRef(
    string BrokerId, string InvestorId, int FrontId, int SessionId, string OrderRef, string ExchangeId);

/// <summary>行情前置连接参数。CTP 行情前置是独立连接，不依赖交易登录即可订阅行情。</summary>
public sealed class CtpMarketDataConnect
{
    public required string MdFrontAddress { get; init; }   // 行情前置 tcp://ip:port
    public required string BrokerId { get; init; }
    public required string UserId { get; init; }
    public required string Password { get; init; }
}
