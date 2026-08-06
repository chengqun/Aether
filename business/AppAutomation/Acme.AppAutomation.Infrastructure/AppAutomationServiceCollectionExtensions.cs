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

        // 固定过程登记（同花顺）。注册为 Singleton<Procedure>，由 ProcedureRunner 构造时经
        // IEnumerable<Procedure> 统一解析并登记进 ProcedureCatalog。未来加「操作 app Y」= 在此注册其 Procedure。
        services.AddSingleton<Procedure>(_ => new ThsConnectProcedure());
        services.AddSingleton<Procedure>(_ => new ThsLaunchProcedure());
        services.AddSingleton<Procedure>(_ => new ThsPlaceOrderProcedure());
        services.AddSingleton<Procedure>(_ => new ThsCancelProcedure());
        services.AddSingleton<Procedure>(_ => new ThsQueryPositionsProcedure());
        services.AddSingleton<Procedure>(_ => new ThsDisconnectProcedure());
        return services;
    }
}
