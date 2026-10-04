using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>订单簿条目：挂单的撮合键（不含数量——量以 <c>SimOrderManager</c> 的 entry 为单一事实源，执行时查）。</summary>
internal sealed record BookEntry(string AccountId, string OrderRef, CtpDirection Direction, double Price, long Seq);

/// <summary>
/// 单合约订单簿（客户端互为对手层——价格发现的唯一来源）。<see cref="SimTradingCore"/> 持有，全 mutation 在 <c>_gate</c> 内——自身非线程安全。
/// <para>
/// <b>不变量</b>：①簿内只有 **GFD 活单**（<c>IsLive &amp;&amp; Remaining &gt; 0</c>）——FOK/IOC 永不入簿；
/// ②<see cref="Seq"/> 全簿严格递增 ⇒ 同价位内时间优先；③静息态簿**非交叉**（买卖互吃由插入时撮合驱动），
/// 唯一例外是同账户锁单（自成交跳过使买卖同账户交叉单都留着）；④重启后簿为空（挂单不复活）；
/// ⑤簿成交**全深度**（簿对手是真单，不套流动性模拟上限）。
/// </para>
/// </summary>
internal sealed class OrderBook
{
    private readonly List<BookEntry> _bids = new();   // 买侧：价高在前，同价先挂在前
    private readonly List<BookEntry> _asks = new();   // 卖侧：价低在前，同价先挂在前
    private long _seq;

    public int Count => _bids.Count + _asks.Count;

    /// <summary>挂单入簿（仅 GFD 活单调；时间序由内部 Seq 保证）。</summary>
    public BookEntry Add(string accountId, string orderRef, CtpDirection direction, double price)
        => Insert(new BookEntry(accountId, orderRef, direction, price, checked(++_seq)));

    /// <summary>出簿（撤销/全成/离队；幂等——不在簿则无操作）。返回是否确实移除了条目。</summary>
    public bool Remove(string accountId, string orderRef)
    {
        var removed = false;
        removed |= _bids.RemoveAll(e => e.AccountId == accountId && e.OrderRef == orderRef) > 0;
        removed |= _asks.RemoveAll(e => e.AccountId == accountId && e.OrderRef == orderRef) > 0;
        return removed;
    }

    /// <summary>买侧快照（价格优先降序、同价时间升序——吃卖单方按此序吃）。</summary>
    public IReadOnlyList<BookEntry> BidSide()
        => _bids.OrderByDescending(e => e.Price).ThenBy(e => e.Seq).ToList();

    /// <summary>卖侧快照（价格优先升序、同价时间升序）。</summary>
    public IReadOnlyList<BookEntry> AskSide()
        => _asks.OrderBy(e => e.Price).ThenBy(e => e.Seq).ToList();

    private BookEntry Insert(BookEntry entry)
    {
        (entry.Direction == CtpDirection.Buy ? _bids : _asks).Add(entry);
        return entry;
    }
}
