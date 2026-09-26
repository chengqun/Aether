namespace Acme.Ctp.Simulator;

/// <summary>
/// 仿真柜台参数（全部有可用默认值；宿主可用环境变量覆盖，见 <c>AETHER_CTP_SIM_*</c>）。
/// </summary>
public sealed record SimOptions
{
    /// <summary>初始资金（每个新投资者账户首次出现时发放）。</summary>
    public double InitialBalance { get; init; } = 1_000_000;

    /// <summary>随机游走 tick 间隔（毫秒）。0 = 不自动跑（测试用手动 <see cref="SimMarketDataEngine.Advance"/> 驱动）。</summary>
    public int TickIntervalMs { get; init; } = 500;

    /// <summary>随机游走随机数种子（固定值 ⇒ 可复现，测试依赖此性质）。</summary>
    public int Seed { get; init; } = 20260925;

    /// <summary>每个 tick 的游走步数上限（以最小变动价位为单位；实际步数在 ±该值内取整）。</summary>
    public int MaxWalkTicks { get; init; } = 3;

    /// <summary>随机游走是否只在交易时段内推进（默认关——仿真的意义就是随时可跑）。</summary>
    public bool GateByTradingTime { get; init; }

    /// <summary>接受合约表外的合约（按品种派生 → 再不行按下单价合成）。false ⇒ 未知合约直接拒单。</summary>
    public bool AcceptUnknownInstruments { get; init; } = true;

    /// <summary>单笔报单的最大成交手数（模拟流动性不足导致的部分成交）。</summary>
    public int MaxFillVolume { get; init; } = 100;

    /// <summary>单次撮合发生部分成交的概率（0 = 一次性全成，1 = 每次最多成交 <see cref="MaxFillVolume"/> 手）。</summary>
    public double PartialFillChance { get; init; }

    /// <summary>仿真数据库路径。null ⇒ 纯内存（不落盘，单测默认）。</summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// 上市合约表（「我就是交易所」的自定义面）：host 上架清单，客户端行情板显示它。
    /// null/空 ⇒ 默认 = <see cref="InstrumentCatalog"/> 种子 9 合约；自定义 id 经解析链（精确→品种派生→合成）落表。
    /// host 环境变量 <c>AETHER_CTP_SIM_BOARD</c>（逗号分隔）。
    /// </summary>
    public IReadOnlyList<string>? ListedInstruments { get; init; }

    /// <summary>交易日历：非交易时段的报单是否直接拒绝（默认关——仿真不挑时段）。</summary>
    public bool RejectOrdersOutsideTradingTime { get; init; }

    /// <summary>从环境变量构造（宿主 DI 用；未设的项取默认值）。</summary>
    public static SimOptions FromEnvironment(string? databasePath)
    {
        return new SimOptions
        {
            DatabasePath = databasePath,
            TickIntervalMs = EnvInt("AETHER_CTP_SIM_TICK_MS", 500),
            Seed = EnvInt("AETHER_CTP_SIM_SEED", 20260925),
            InitialBalance = EnvDouble("AETHER_CTP_SIM_BALANCE", 1_000_000),
            ListedInstruments = ParseBoard(Environment.GetEnvironmentVariable("AETHER_CTP_SIM_BOARD")),
        };
    }

    /// <summary>上市合约表解析：逗号分割、去空格、弃空项；空白/坏值 ⇒ null（默认种子表——交易所清单坏值容忍回默认，不拒启动）。</summary>
    private static IReadOnlyList<string>? ParseBoard(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Length == 0 ? null : ids;
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    private static double EnvDouble(string name, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}
