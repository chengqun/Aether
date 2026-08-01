using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
        // McpServerRegistry（IMcpServerRegistry）收集所有 McpServerConfig；首次 AddMcpServer 时注册（幂等）。
        if (!builder.Services.Any(d => d.ServiceType == typeof(IMcpServerRegistry)))
        {
            builder.Services.AddSingleton<McpServerRegistry>(sp =>
                new McpServerRegistry(sp.GetServices<McpServerConfig>(), sp.GetService<ILoggerFactory>()));
            builder.Services.AddSingleton<IMcpServerRegistry>(sp => sp.GetRequiredService<McpServerRegistry>());
            // 工具 accessor：AgentFactory 经此同步取已预解析的 MCP 工具（StartAsync 前为空）。
            builder.Services.AddSingleton<Func<IReadOnlyList<AIFunction>>>(sp =>
                () => sp.GetRequiredService<IMcpServerRegistry>().GetTools());
        }
        return builder;
    }

    /// <summary>
    /// 从目录扫描 MCP server 配置文件（<c>*.json</c>，每个文件一个对象或一个数组）并注册。
    /// 目录不存在则创建（空目录 = 无 MCP server）。镜像 skills 目录的模式：放配置文件即可，无需改代码。
    /// </summary>
    public static AetherAIBuilder AddMcpServersFromDirectory(this AetherAIBuilder builder, string directory)
    {
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        foreach (var cfg in McpServerConfigLoader.LoadFromDirectory(directory))
            builder.AddMcpServer(cfg);
        return builder;
    }
}

/// <summary>
/// 从目录加载 <see cref="McpServerConfig"/>：扫描 <c>*.json</c>，每个文件可为单个对象或对象数组。
/// 格式示例（stdio）：{"name":"fs","command":"npx","arguments":["-y","@modelcontextprotocol/server-filesystem","/tmp"]}
/// （SSE）：{"name":"remote","url":"https://host/sse"}。解析失败的文件跳过。
/// </summary>
public static class McpServerConfigLoader
{
    public static List<McpServerConfig> LoadFromDirectory(string directory)
    {
        var list = new List<McpServerConfig>();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return list;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        list.Add(ParseConfig(el));
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    list.Add(ParseConfig(doc.RootElement));
                }
            }
            catch
            {
                /* 解析失败的配置文件跳过，不阻断启动 */
            }
        }
        return list;
    }

    private static McpServerConfig ParseConfig(JsonElement el)
    {
        string? Get(string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        IReadOnlyList<string>? args = null;
        if (el.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array)
            args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();

        return new McpServerConfig
        {
            Name = Get("name") ?? string.Empty,
            Command = Get("command"),
            Arguments = args,
            WorkingDirectory = Get("workingDirectory"),
            Url = Get("url"),
        };
    }
}

