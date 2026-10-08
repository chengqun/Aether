using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Storage;

/// <summary>资金账户快照（落盘行）。<paramref name="PasswordHash"/> null = 未设密码（免认证——dev/测试路径）。</summary>
public sealed record SimAccountSnapshot(
    string AccountId,
    double StaticBalance,
    double CurrMargin,
    double FrozenMargin,
    double FrozenCash,
    double Commission,
    double CloseProfit,
    double PositionProfit,
    string? PasswordHash = null,
    string? BrokerId = null);

/// <summary>持仓快照（落盘行）。<paramref name="Direction"/> 为 <c>"L"</c>/<c>"S"</c>。</summary>
public sealed record SimPositionSnapshot(
    string InstrumentId,
    string ExchangeId,
    string Direction,
    int Today,
    int Yesterday,
    double Cost,
    double Margin);

/// <summary>
/// 现货铸造台账（落盘行）：该账户在该现货合约上的<b>已净铸造手数</b>（充币 +/提币烧毁 −，下限 0）。
/// <para>
/// 与持仓行分表存——铸造量是<b>托管归账</b>（「交易所欠这个账户多少币」），持仓是<b>可交易物</b>：
/// 交易（买开/卖平）只转移持仓不改变铸造量；全平仓后持仓行会被删（快照只为非零持仓生成行），
/// 铸造量行必须独立存活，否则重启对账会把「已卖出的存量」误判为「未铸造」而重复铸造。
/// </para>
/// <para>桥对账的 DB 侧真相：<c>目标 = 链侧 IOU</c>，<c>现值 = 本台账</c>，差值驱动补铸/烧毁（见 host 的 LedgerSpotLink）。</para>
/// </summary>
public sealed record SpotMintedSnapshot(string AccountId, string InstrumentId, int MintedLots);

/// <summary>
/// 计价币铸造台账（批 6 双资产）：账户已净充入的 AQ 原子数（充值 + / 提现扣 −，下限 0）。
/// 与币侧台账同构——链侧 AQ IOU 是对账目标；**场内现金（StaticBalance）不是铸造台账**
///（现金随交易/盈亏/手续费浮动，台账只记链上进出）。
/// </summary>
public sealed record QuoteMintedSnapshot(string AccountId, long MintedAtoms);

/// <summary>
/// 仿真柜台持久化端口。
/// <para>
/// 落在 Domain 之外的自己的命名空间里（仿真核心的内部协作面，非业务契约）——
/// 不进 <c>Acme.Ctp.Domain</c>：真柜台不需要它，且这样单测可以直接用 <see cref="NullSimStore"/> 零磁盘跑。
/// </para>
/// <para>
/// <b>全部是「尽力而为」</b>：核心调用时 try/catch 吞掉异常（只 <c>Trace</c>），
/// 落盘失败绝不影响交易链路——仿真柜台是开发工具，不该因为磁盘满而拒单。
/// </para>
/// </summary>
public interface ISimStore
{
    /// <summary>建表（幂等）。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SimAccountSnapshot>> LoadAccountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SimPositionSnapshot>> LoadPositionsAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<CtpOrder>> LoadOrdersAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<SimInstrument>> LoadInstrumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// 该账户已落盘的全部成交号。
    /// <para>
    /// 只用来把成交号序列恢复到已用最大值之上——成交明细本身**不重载**进核心
    /// （没有消费者，且重载会牵出「历史成交要不要重算持仓」的复杂度）。
    /// 但序列必须续上：<see cref="SaveTradeAsync"/> 按 <c>(account_id, trade_id)</c> 写入，
    /// 序列从 1 重新开始会**静默覆盖**上次运行的成交记录。
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> LoadTradeIdsAsync(string accountId, CancellationToken ct = default);

    Task SaveAccountAsync(SimAccountSnapshot snapshot, CancellationToken ct = default);
    Task SavePositionsAsync(string accountId, IReadOnlyList<SimPositionSnapshot> positions, CancellationToken ct = default);
    Task SaveOrderAsync(string accountId, CtpOrder order, CancellationToken ct = default);
    Task SaveTradeAsync(string accountId, CtpTrade trade, CancellationToken ct = default);
    Task SaveInstrumentAsync(SimInstrument instrument, CancellationToken ct = default);

    /// <summary>读全部现货铸造台账行（启动恢复——核心按合约分组）。</summary>
    Task<IReadOnlyList<SpotMintedSnapshot>> LoadSpotMintedAsync(CancellationToken ct = default);

    /// <summary>写一行现货铸造台账（upsert——(account, instrument) 主键）。</summary>
    Task SaveSpotMintedAsync(string instrumentId, string accountId, int mintedLots, CancellationToken ct = default);

    /// <summary>读全部计价币铸造台账行（启动恢复——核心按账户分桶）。</summary>
    Task<IReadOnlyList<QuoteMintedSnapshot>> LoadQuoteMintedAsync(CancellationToken ct = default);

    /// <summary>写一行计价币铸造台账（upsert——account 主键）。</summary>
    Task SaveQuoteMintedAsync(string accountId, long mintedAtoms, CancellationToken ct = default);

    /// <summary>追加一条成交 tick（真实交易消息——交易所模式每笔成交的完整快照：价/量/盘口/开高低）。</summary>
    Task SaveTickAsync(CtpTick tick, CancellationToken ct = default);

    /// <summary>读某合约最近 <paramref name="limit"/> 条成交 tick（时间升序——末位最新；重启恢复取 limit=1）。</summary>
    Task<IReadOnlyList<CtpTick>> LoadRecentTicksAsync(string instrumentId, int limit, CancellationToken ct = default);
}

/// <summary>不落盘（默认）——单测与「纯内存仿真」用。</summary>
public sealed class NullSimStore : ISimStore
{
    public static readonly NullSimStore Instance = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SimAccountSnapshot>> LoadAccountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SimAccountSnapshot>>(Array.Empty<SimAccountSnapshot>());
    public Task<IReadOnlyList<SimPositionSnapshot>> LoadPositionsAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SimPositionSnapshot>>(Array.Empty<SimPositionSnapshot>());
    public Task<IReadOnlyList<CtpOrder>> LoadOrdersAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CtpOrder>>(Array.Empty<CtpOrder>());
    public Task<IReadOnlyList<SimInstrument>> LoadInstrumentsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SimInstrument>>(Array.Empty<SimInstrument>());
    public Task<IReadOnlyList<string>> LoadTradeIdsAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task SaveAccountAsync(SimAccountSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
    public Task SavePositionsAsync(string accountId, IReadOnlyList<SimPositionSnapshot> positions, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveOrderAsync(string accountId, CtpOrder order, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveTradeAsync(string accountId, CtpTrade trade, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveInstrumentAsync(SimInstrument instrument, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SpotMintedSnapshot>> LoadSpotMintedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SpotMintedSnapshot>>(Array.Empty<SpotMintedSnapshot>());
    public Task SaveSpotMintedAsync(string instrumentId, string accountId, int mintedLots, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<QuoteMintedSnapshot>> LoadQuoteMintedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QuoteMintedSnapshot>>(Array.Empty<QuoteMintedSnapshot>());
    public Task SaveQuoteMintedAsync(string accountId, long mintedAtoms, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SaveTickAsync(CtpTick tick, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CtpTick>> LoadRecentTicksAsync(string instrumentId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CtpTick>>(Array.Empty<CtpTick>());
}
