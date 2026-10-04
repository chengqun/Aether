namespace Acme.Ctp.Simulator;

/// <summary>
/// CTP 报单状态字符码（<c>CtpOrder.OrderStatusRaw</c> 的取值）。
/// <para>
/// 只用到其中 5 个（Phase 1 无「已撤销待确认」这类中间态），其余列出便于对照协议。
/// 客户端的 <c>CtpSessionMapper</c> 直接强转 char，不做语义解释。
/// </para>
/// </summary>
public static class SimOrderStatus
{
    /// <summary>全部成交。</summary>
    public const byte AllTraded = (byte)'0';
    /// <summary>部分成交还在队列中（可继续成交 / 可撤）。</summary>
    public const byte PartTradedQueueing = (byte)'1';
    /// <summary>部分成交不在队列中。</summary>
    public const byte PartTradedNotQueueing = (byte)'2';
    /// <summary>未成交还在队列中（**已接受、挂单中**）。</summary>
    public const byte NoTradeQueueing = (byte)'3';
    /// <summary>未成交不在队列中（IOC/FOK 未成交后即废）。</summary>
    public const byte NoTradeNotQueueing = (byte)'4';
    /// <summary>已撤销。</summary>
    public const byte Canceled = (byte)'5';
    /// <summary>未知。</summary>
    public const byte Unknown = (byte)'a';
    /// <summary>尚未触发。</summary>
    public const byte NotTouched = (byte)'b';
    /// <summary>已触发。</summary>
    public const byte Touched = (byte)'c';

    /// <summary>是否仍在交易所队列中（可撤单）。</summary>
    public static bool IsLive(byte status) => status is NoTradeQueueing or PartTradedQueueing;

    /// <summary>
    /// 是否已到终态（不可再变动）。
    /// <para><c>'2'</c> 也算终态——「部分成交不在队列」意味着残量作废，之后既不会再有成交也不能撤单。</para>
    /// </summary>
    public static bool IsTerminal(byte status)
        => status is AllTraded or PartTradedNotQueueing or Canceled or NoTradeNotQueueing;

    /// <summary>便于日志/状态文本。</summary>
    public static string Describe(byte status) => status switch
    {
        AllTraded => "全部成交",
        PartTradedQueueing => "部分成交",
        PartTradedNotQueueing => "部分成交(不在队列)",
        NoTradeQueueing => "已报入(未成交)",
        NoTradeNotQueueing => "已报入(未成交,不在队列)",
        Canceled => "已撤销",
        _ => $"状态{(char)status}",
    };
}
