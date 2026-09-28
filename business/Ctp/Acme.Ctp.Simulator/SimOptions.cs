namespace Acme.Ctp.Simulator;

/// <summary>价格源模式（host 环境变量 <c>AETHER_CTP_SIM_MODE</c>）。</summary>
public enum SimMarketMode
{
    /// <summary>
    /// 交易所模式（**默认**，2026-09-28 用户定调「我实际就是做一个交易所」）——**价格发现归簿**：
    /// bid/ask = 订单簿顶（真实挂单），last = 最新成交价（首笔前 = 昨结算基准），tick 事件驱动
    /// （挂单/撤单/成交即推）。庄家兜底关闭（市价单空簿拒单、市价残量作废不挂单），
    /// 流动性由做市机器人供给（<see cref="SimOptions.MarketMakerBot"/>，默认开）。
    /// </summary>
    Exchange = 0,

    /// <summary>
    /// 随机游走——**显式退路**（`AETHER_CTP_SIM_MODE=walk`，旧行为）：定时器推进中枢价，庄家兜底用游走报价撮合。
    /// 也是历史 tick 回放的宿主机制（回放经 <c>InjectQuote</c> 注价——交易所模式下 no-op，
    /// 故 <c>SimHostOptions</c> 在 REPLAY 设定时强制归一到本模式）。
    /// </summary>
    Walk = 1,
}

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
    /// null/空 ⇒ 默认 = sim001（本交易所自有品种）；自定义 id 经解析链（精确→品种派生→合成）落表。
    /// host 环境变量 <c>AETHER_CTP_SIM_BOARD</c>（逗号分隔）。
    /// </summary>
    public IReadOnlyList<string>? ListedInstruments { get; init; }

    /// <summary>历史 tick 回放 CSV 路径（确定性测试的价格源——设定即回放模式，host 层强制归一 walk 撮合）。host 环境变量 <c>AETHER_CTP_SIM_REPLAY</c>。</summary>
    public string? ReplayPath { get; init; }

    /// <summary>回放速度倍率（&gt;1 快放、&lt;1 慢放；坏值 ⇒ 1）。host 环境变量 <c>AETHER_CTP_SIM_REPLAY_SPEED</c>。</summary>
    public double ReplaySpeed { get; init; } = 1;

    /// <summary>回放到文件尾是否循环（默认 true——沙盒常有市；文件尾停 1s 再来）。host 环境变量 <c>AETHER_CTP_SIM_REPLAY_LOOP</c>。</summary>
    public bool ReplayLoop { get; init; } = true;

    /// <summary>价格源模式（默认 = 交易所模式——「自己的交易所」；walk 为显式退路）。host 环境变量 <c>AETHER_CTP_SIM_MODE</c>。</summary>
    public SimMarketMode MarketMode { get; init; } = SimMarketMode.Exchange;

    /// <summary>
    /// 交易所模式下的做市机器人（默认开——纯簿模式无人报价时市场全死）。它是**普通账户**走完整
    /// 风控/清算，围绕最新成交价在簿两侧挂<b>可见限价单</b>——区别于旧「庄家兜底」（无限流动性 +
    /// 游走随机价的暗对手）：占真实簿档位、留成交记录、资金守恒，价格发现仍归簿。host 环境变量 <c>AETHER_CTP_SIM_MM</c>。
    /// </summary>
    public bool MarketMakerBot { get; init; } = true;

    /// <summary>做市机器人报价点差（以最小变动价位计，围绕最新成交价 ±N；默认 2）。host 环境变量 <c>AETHER_CTP_SIM_MM_SPREAD</c>。</summary>
    public int MmSpreadTicks { get; init; } = 2;

    /// <summary>做市机器人单侧挂单手数（默认 5）。host 环境变量 <c>AETHER_CTP_SIM_MM_VOLUME</c>。</summary>
    public int MmVolume { get; init; } = 5;

    // —— 噪声交易者（模拟成交——让行情「活着」的程序化参与者）——

    /// <summary>
    /// 模拟成交（默认开）：随机吃做市流动力的程序化参与者（账户 <c>SIM-NOISE</c>）——**真实下单、真实成交、
    /// 真实记账**，价格经真实成交移动（区别于旧随机游走的凭空画价）。让盘口有成交、最新价/成交量持续更新。
    /// host 环境变量 <c>AETHER_CTP_SIM_NOISE</c>。
    /// </summary>
    public bool NoiseTrading { get; init; } = true;

    /// <summary>噪声交易间隔（毫秒，默认 2500；host 侧下限 500）。host 环境变量 <c>AETHER_CTP_SIM_NOISE_MS</c>。</summary>
    public int NoiseIntervalMs { get; init; } = 2500;

    /// <summary>噪声交易单笔手数上限（默认 3；净持仓软钳同值——超界反转方向 ⇒ 持仓/OI 有界）。host 环境变量 <c>AETHER_CTP_SIM_NOISE_MAX_LOTS</c>。</summary>
    public int NoiseMaxLots { get; init; } = 3;

    // —— 做市商义务（交易所约束层——真实做市协议的核心条款；策略层各家自便，约束层人人须守）——

    /// <summary>义务：最大报价价差（买一卖一总差，tick 数；&lt;2 视为 2——买卖不可同价自锁）。host 环境变量 <c>AETHER_CTP_SIM_MM_MAX_SPREAD</c>。</summary>
    public int MmMaxSpreadTicks { get; init; } = 4;

    /// <summary>义务：单侧最小报价量（手）。host 环境变量 <c>AETHER_CTP_SIM_MM_MIN_VOLUME</c>。</summary>
    public int MmMinQuoteVolume { get; init; } = 1;

    /// <summary>义务：双边连续报价时间占比目标（监测口径 0-1，仅统计不强制；触限单边期豁免不计入）。host 环境变量 <c>AETHER_CTP_SIM_MM_MIN_RATIO</c>。</summary>
    public double MmMinQuoteRatio { get; init; } = 0.9;

    /// <summary>义务：做市商持仓限额（每合约<b>单边</b>手数）——触限停挂加仓侧（「超限禁开」等价）、
    /// 超限自动挂平仓单减仓（交易所强平的沙盒等价）。host 环境变量 <c>AETHER_CTP_SIM_MM_POS_LIMIT</c>。</summary>
    public int MmPositionLimit { get; init; } = 200;

    /// <summary>义务监测采样间隔（毫秒；测试可调小）。host 环境变量 <c>AETHER_CTP_SIM_MM_MONITOR_MS</c>。</summary>
    public int MmMonitorIntervalMs { get; init; } = 1000;

    // —— 账号系统（自助开户：首次登录设密码；密码空 = 免认证——dev/测试路径）——

    /// <summary>密码哈希迭代数（PBKDF2-SHA256；公网部署前可调高）。仅设密/验密路径付费，空密码登录零成本。</summary>
    public int PasswordIterations { get; init; } = 50_000;

    /// <summary>防爆破：连续登录失败 N 次锁定。host 环境变量 <c>AETHER_CTP_SIM_LOGIN_MAX_FAILURES</c>。</summary>
    public int LoginMaxFailures { get; init; } = 5;

    /// <summary>防爆破：锁定时长（秒，按 SimClock 计）。host 环境变量 <c>AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC</c>。</summary>
    public int LoginLockoutSeconds { get; init; } = 60;

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
            ReplayPath = EnvText("AETHER_CTP_SIM_REPLAY"),
            ReplaySpeed = EnvDouble("AETHER_CTP_SIM_REPLAY_SPEED", 1) > 0 ? EnvDouble("AETHER_CTP_SIM_REPLAY_SPEED", 1) : 1,
            ReplayLoop = ParseBool("AETHER_CTP_SIM_REPLAY_LOOP", defaultValue: true),
            MarketMode = ParseMode(Environment.GetEnvironmentVariable("AETHER_CTP_SIM_MODE")),
            MarketMakerBot = ParseBool("AETHER_CTP_SIM_MM", defaultValue: true),
            MmSpreadTicks = Math.Max(1, EnvInt("AETHER_CTP_SIM_MM_SPREAD", 2)),
            MmVolume = Math.Max(1, EnvInt("AETHER_CTP_SIM_MM_VOLUME", 5)),
            MmMaxSpreadTicks = Math.Max(2, EnvInt("AETHER_CTP_SIM_MM_MAX_SPREAD", 4)),
            MmMinQuoteVolume = Math.Max(1, EnvInt("AETHER_CTP_SIM_MM_MIN_VOLUME", 1)),
            MmMinQuoteRatio = Math.Clamp(EnvDouble("AETHER_CTP_SIM_MM_MIN_RATIO", 0.9), 0, 1),
            MmPositionLimit = Math.Max(1, EnvInt("AETHER_CTP_SIM_MM_POS_LIMIT", 200)),
            MmMonitorIntervalMs = Math.Max(50, EnvInt("AETHER_CTP_SIM_MM_MONITOR_MS", 1000)),
            NoiseTrading = ParseBool("AETHER_CTP_SIM_NOISE", defaultValue: true),
            NoiseIntervalMs = EnvInt("AETHER_CTP_SIM_NOISE_MS", 2500),
            NoiseMaxLots = Math.Max(1, EnvInt("AETHER_CTP_SIM_NOISE_MAX_LOTS", 3)),
            LoginMaxFailures = Math.Max(1, EnvInt("AETHER_CTP_SIM_LOGIN_MAX_FAILURES", 5)),
            LoginLockoutSeconds = Math.Max(0, EnvInt("AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC", 60)),
        };
    }

    private static string? EnvText(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>价格源模式解析：空白 ⇒ Exchange（默认）；<c>exchange</c>/<c>walk</c>（大小写不敏感）⇒ 对应值；
    /// 其它非空值 ⇒ <see cref="ArgumentException"/>（两值枚举打错字宁可知晓——静默落错市场比拒启动更糟）。</summary>
    private static SimMarketMode ParseMode(string? v)
    {
        var t = v?.Trim();
        if (string.IsNullOrEmpty(t)) return SimMarketMode.Exchange;
        if (string.Equals(t, "exchange", StringComparison.OrdinalIgnoreCase)) return SimMarketMode.Exchange;
        if (string.Equals(t, "walk", StringComparison.OrdinalIgnoreCase)) return SimMarketMode.Walk;
        throw new ArgumentException($"AETHER_CTP_SIM_MODE 格式非法：\"{v}\"（应为 exchange 或 walk；不设 = 默认 exchange）");
    }

    private static bool ParseBool(string name, bool defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v)
            ? defaultValue
            : v.Trim() is "1" or "true" or "on" or "yes";
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
