namespace Acme.Ctp.Simulator;

/// <summary>
/// 仿真交易所参数（全部有可用默认值；宿主可用环境变量覆盖，见 <c>AETHER_CTP_SIM_*</c>）。
/// 2026-10-03：walk 模式已删除——交易所恒为订单簿价格发现（价格来自真实挂单/成交），
/// tick 回放已删除（回放骑 walk 撮合机制，walk 删 ⇒ 回放无宿主）。
/// </summary>
public sealed record SimOptions
{
    /// <summary>初始资金（每个新投资者账户首次出现时发放）。host 环境变量 <c>AETHER_CTP_SIM_BALANCE</c>。</summary>
    public double InitialBalance { get; init; } = 1_000_000;

    /// <summary>仿真数据库路径。null ⇒ 纯内存（不落盘，单测默认）。</summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// 上市合约表（「我就是交易所」的自定义面）：host 上架清单，客户端行情板显示它。
    /// null/空 ⇒ 默认 = zx001（本交易所自有品种，官方命名合规 2 字母+3 数字）；自定义 id 经解析链（精确→品种派生→合成）落表。
    /// host 环境变量 <c>AETHER_CTP_SIM_BOARD</c>（逗号分隔）。
    /// </summary>
    public IReadOnlyList<string>? ListedInstruments { get; init; }

    // —— 账号系统（自助开户：首次登录设密码；密码必填——空/空白一律拒 90017）——

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
            InitialBalance = EnvDouble("AETHER_CTP_SIM_BALANCE", 1_000_000),
            ListedInstruments = ParseBoard(Environment.GetEnvironmentVariable("AETHER_CTP_SIM_BOARD")),
            LoginMaxFailures = Math.Max(1, EnvInt("AETHER_CTP_SIM_LOGIN_MAX_FAILURES", 5)),
            LoginLockoutSeconds = Math.Max(0, EnvInt("AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC", 60)),
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
