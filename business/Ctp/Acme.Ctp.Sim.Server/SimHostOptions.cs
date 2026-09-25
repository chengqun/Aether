using Acme.Ctp.Sim;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Sim.Server;

/// <summary>
/// 仿真柜台宿主参数。放在 lib（而非 Host exe）里是为了可单测——
/// db 路径/地址解析不须起进程就能验证。
/// </summary>
public sealed record SimHostOptions(string Host, int Port, SimOptions Core)
{
    /// <summary>仿真库路径覆盖（默认 <c>~/.aether/ctp-sim.db</c>）。</summary>
    public const string DbEnvVar = "AETHER_CTP_SIM_DB";

    /// <summary>
    /// 从环境变量构造：<c>AETHER_CTP_SIM_ADDR</c>（监听端点）、<c>AETHER_CTP_SIM_DB</c>（库路径），
    /// 其余（tick 间隔/种子/初始资金/MD 模式）经 <see cref="SimOptions.FromEnvironment"/>。
    /// </summary>
    public static SimHostOptions FromEnvironment()
    {
        var (host, port) = SimAddress.Parse(Environment.GetEnvironmentVariable(SimAddress.EnvVar));
        var db = Environment.GetEnvironmentVariable(DbEnvVar);
        if (string.IsNullOrWhiteSpace(db)) db = DefaultDatabasePath();
        return new SimHostOptions(host, port, SimOptions.FromEnvironment(db));
    }

    /// <summary>
    /// 默认库位置 <c>~/.aether/ctp-sim.db</c>——与真实 <c>ctp.db</c>（<c>~/.aether/ctp.db</c>）同目录不同文件。
    /// 真实库存**明文 CTP 密码**，仿真账本绝不可写进同一文件。
    /// </summary>
    public static string DefaultDatabasePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "ctp-sim.db");
}
