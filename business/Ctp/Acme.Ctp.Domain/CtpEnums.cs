namespace Acme.Ctp.Domain;

// —— CTP 交易枚举（byte 基，值镜像 CTP 协议 char 码，便于 Infrastructure 侧与 SDK 直接强转）——

/// <summary>买卖方向。</summary>
public enum CtpDirection : byte { Buy = (byte)'0', Sell = (byte)'1' }

/// <summary>开平标志。</summary>
public enum CtpOffsetFlag : byte
{
    Open = (byte)'0',
    Close = (byte)'1',
    CloseToday = (byte)'3',
    CloseYesterday = (byte)'4',
}

/// <summary>报单价格类型。</summary>
public enum CtpOrderPriceType : byte { Market = (byte)'0', Limit = (byte)'2' }

/// <summary>有效期类型。</summary>
public enum CtpTimeCondition : byte { IOC = (byte)'1', FOK = (byte)'2', GFD = (byte)'3' }

/// <summary>持仓方向。</summary>
public enum CtpPosiDirection : byte { Net = (byte)'1', Long = (byte)'2', Short = (byte)'3' }

/// <summary>投机套保标志。</summary>
public enum CtpHedgeFlag : byte { Speculation = (byte)'1', Arbitrage = (byte)'2', Hedge = (byte)'3' }

/// <summary>持仓日期（今/昨）。</summary>
public enum CtpPositionDate : byte { Today = (byte)'1', History = (byte)'2' }

/// <summary>
/// CTP 连接状态（比单纯 bool 更细，承载重连/闭市语义）。
/// </summary>
public enum CtpConnectionStatus
{
    /// <summary>未登录 / 已登出。</summary>
    Disconnected,
    /// <summary>已连接且已登录（可交易/查询）。</summary>
    Connected,
    /// <summary>盘中断线，重连 + 重新登录中。</summary>
    Reconnecting,
    /// <summary>闭市（交易时段外），等待下个开盘自动重连。</summary>
    Closed,
}
