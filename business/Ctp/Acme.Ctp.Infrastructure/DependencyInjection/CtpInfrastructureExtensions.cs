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
/// <b>一律官方 SDK 链路</b>（2026-10-01 起）：<see cref="ICtpSession"/> 直连 <see cref="CtpSessionService"/>——
/// 本地仿真柜台填前置 <c>tcp://127.0.0.1:17801</c>（交易）/ <c>tcp://127.0.0.1:17802</c>（行情，host 进程
/// <c>dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host</c>）；真实前置（SimNow 等）同一链路。
/// 旧 <c>sim</c> 关键字 JSON-RPC 直连已退役（RoutingCtpSession/RemoteCtpSession 不再进 DI）。
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

        // 2026-10-01 用户定调：桌面端砍掉 sim 关键字直连（JSON-RPC），一律走前置端口——
        // ICtpSession 直连官方 SDK 链路（本地前置 tcp://127.0.0.1:17801/17802 或真实 SimNow 同链路）。
        // RoutingCtpSession/RemoteCtpSession 退役出 DI（类保留——host 侧 E2E 测试仍直接构造使用）。
        services.AddSingleton<CtpSessionService>();
        services.AddSingleton<ICtpSession>(sp => sp.GetRequiredService<CtpSessionService>());

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
