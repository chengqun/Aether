using System.IO;
using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Acme.Ctp.Infrastructure.Sim;
using Acme.Ctp.Sim;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the CTP infrastructure layer. Registers the CTP session
/// (the connection to the CTP broker) + the SQLite config store for credentials.
/// <para>
/// <b>「用前置地址选柜台」</b>：<see cref="ICtpSession"/> 唯一注册为 <see cref="RoutingCtpSession"/>——
/// 前置地址填 <c>sim</c>（或默认仿真端点 127.0.0.1:17800，判据 <see cref="SimAddress.IsSimFront"/>）⇒
/// 连独立 host 进程（<c>dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host</c>）；填真实前置 ⇒ 官方链路。
/// 无环境变量、无重启——切柜台 = 换前置地址重新登录（地址随凭据存 ctp.db，自动登录自动记住）。
/// 仿真核心的配置（DB/种子/tick/初始资金/MD 模式）在 **host 进程**侧读。
/// </para>
/// </summary>
public static class CtpInfrastructureExtensions
{
    /// <summary>客户端侧仿真 RPC 端点覆盖（可选；格式 <c>[host:]port</c>，默认 <c>127.0.0.1:17800</c>）。host 进程读同名变量。</summary>
    public const string SimAddrEnvVar = SimAddress.EnvVar;

    /// <param name="sqliteDbPath">SQLite file for the ctp_config table. Falls back to <c>CTP_DB_PATH</c>, then <c>~/.aether/ctp.db</c>.</param>
    public static IServiceCollection AddCtpInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("CTP_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "ctp.db");

        // 两个后端都常驻（构造全惰性：不起连接、不 P/Invoke），由 RoutingCtpSession 按登录地址逐次路由
        services.AddSingleton<CtpSessionService>();
        services.AddSingleton(_ => new RemoteCtpSession(ClientSimEndpoint().Host, ClientSimEndpoint().Port));
        services.AddSingleton<ICtpSession>(sp => new RoutingCtpSession(
            sp.GetRequiredService<CtpSessionService>(),
            sp.GetRequiredService<RemoteCtpSession>()));

        services.AddSingleton<ICtpConfigStore>(_ => SqliteCtpConfigStore.ForFile(dbPath));
        return services;
    }

    /// <summary>
    /// 客户端侧仿真端点：读 <see cref="SimAddrEnvVar"/>（可选覆盖）。<b>坏值容忍回默认</b>——
    /// 路由形态下 app 启动即构造仿真后端，一个垃圾环境变量不能拖垮从不连仿真的真实用户（与 host 侧的
    /// 严格抛错刻意不同：host 是显式启动的工具进程，fail-fast 合理；客户端常驻，容忍合理）。
    /// </summary>
    private static (string Host, int Port) ClientSimEndpoint()
    {
        try
        {
            return SimAddress.Parse(Environment.GetEnvironmentVariable(SimAddrEnvVar));
        }
        catch (FormatException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[CTP-SIM] {SimAddrEnvVar} 值非法，已回退默认端点 {SimEndpointDefaults.Host}:{SimEndpointDefaults.Port}。");
            return (SimEndpointDefaults.Host, SimEndpointDefaults.Port);
        }
    }
}
