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

/// <summary>
/// 有效期类型。'1'/'2'/'3' 对齐真实 CTP；<see cref="PostOnly"/> ('4') 为**本地协议扩展值**（非真实 CTP TC——
/// crypto post-only/GTX 语义：GFD + 插入时不得交叉，会吃单整单拒收，保证永远付 maker 费率，2026-10-05 BTC 对标）。
/// ⑲ wire 侧经量字段 run 尾字节透传（front codec VolShape 白名单须含 '4'）。
/// </summary>
public enum CtpTimeCondition : byte { IOC = (byte)'1', FOK = (byte)'2', GFD = (byte)'3', PostOnly = (byte)'4' }

/// <summary>持仓方向。</summary>
public enum CtpPosiDirection : byte { Net = (byte)'1', Long = (byte)'2', Short = (byte)'3' }

/// <summary>投机套保标志。</summary>
public enum CtpHedgeFlag : byte { Speculation = (byte)'1', Arbitrage = (byte)'2', Hedge = (byte)'3' }

/// <summary>持仓日期（今/昨）。</summary>
public enum CtpPositionDate : byte { Today = (byte)'1', History = (byte)'2' }

// CtpConnectionStatus（客户端会话连接状态）在 business/Ctp/Acme.Ctp.Domain/CtpClientContracts.cs——
// 客户端专属类型（2026-10-04 契约分家：内核只留三方共享的交易域模型）。
