using Acme.AppAutomation.Application;
using Acme.AppAutomation.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// AppAutomation 的 DI 入口。注册过程目录 + 运行器 + <see cref="IDesktopAutomation"/> 的 MCP 实现，
/// 并把同花顺固定过程登记进目录。
/// <para><b>不</b>注册 <see cref="IMcpToolInvoker"/>——它包 <c>McpClient</c>（MCP alpha 包，隔离在
/// <c>Aether.AI.Mcp</c>），由宿主在 Windows/Mac 侧注册（连具体桌面控制 MCP server）。测试用 fake。</para>
/// </summary>
public static class AppAutomationServiceCollectionExtensions
{
    public static IServiceCollection AddAppAutomation(this IServiceCollection services)
    {
        services.AddSingleton<ProcedureCatalog>();
        services.AddSingleton<IProcedureRunner, ProcedureRunner>();
        services.AddSingleton<IDesktopAutomation, McpDesktopAutomation>();

        // 固定过程登记（同花顺）。未来加「操作 app Y」= 在此注册其 Procedure。
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsConnectProcedure()));
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsLaunchProcedure()));
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsPlaceOrderProcedure()));
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsCancelProcedure()));
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsQueryPositionsProcedure()));
        services.AddSingleton<Procedure>(sp => Register(sp, new ThsDisconnectProcedure()));
        return services;
    }

    /// <summary>把过程登记进目录并返回它（供 DI 单例持有）。</summary>
    private static Procedure Register(IServiceProvider sp, Procedure procedure)
    {
        sp.GetRequiredService<ProcedureCatalog>().Register(procedure);
        return procedure;
    }
}
