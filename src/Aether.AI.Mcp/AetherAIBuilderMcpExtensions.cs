using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Mcp;

/// <summary>
/// <see cref="AetherAIBuilder"/> 的 MCP 扩展：注册 MCP server 配置 + <see cref="McpServerRegistry"/>，
/// 并把工具 accessor（<c>Func&lt;IReadOnlyList&lt;AIFunction&gt;&gt;</c>）注册到 DI 供 AgentFactory 取用。
/// </summary>
public static class AetherAIBuilderMcpExtensions
{
    /// <summary>添加一个 MCP server（stdio 或 SSE）。可多次调用注册多个。</summary>
    public static AetherAIBuilder AddMcpServer(this AetherAIBuilder builder, McpServerConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        builder.Services.AddSingleton(config);
        // McpServerRegistry 收集所有 McpServerConfig；首次 AddMcpServer 时注册（幂等，避免重复注册）。
        if (!builder.Services.Any(d => d.ServiceType == typeof(McpServerRegistry)))
        {
            builder.Services.AddSingleton<McpServerRegistry>(sp =>
                new McpServerRegistry(sp.GetServices<McpServerConfig>(), sp.GetService<ILoggerFactory>()));
            // 工具 accessor：AgentFactory 经此同步取已预解析的 MCP 工具（StartAsync 前为空）。
            builder.Services.AddSingleton<Func<IReadOnlyList<AIFunction>>>(sp =>
                () => sp.GetRequiredService<McpServerRegistry>().GetTools());
        }
        return builder;
    }
}
