using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Storage;

/// <summary>资金账户快照（落盘行）。</summary>
public sealed record SimAccountSnapshot(
    string AccountId,
    double StaticBalance,
    double CurrMargin,
    double FrozenMargin,
    double FrozenCash,
    double Commission,
    double CloseProfit,
    double PositionProfit);

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
}
