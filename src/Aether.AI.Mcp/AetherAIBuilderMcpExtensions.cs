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
/// 从目录加载 <see cref="McpServerConfig"/>：扫描 <c>*.json</c>。兼容三种格式：
/// <list type="bullet">
/// <item><b>主流 mcpServers 映射</b>（Claude Code / Cursor 的 <c>.mcp.json</c>，可直接复用）：
///   <c>{ "mcpServers": { "name": { "command","args","env","url","headers" } } }</c></item>
/// <item><b>单个 server 对象</b>：<c>{ "name","command","args","env","url","headers" }</c></item>
/// <item><b>数组</b>：<c>[ { ... }, { ... } ]</c></item>
/// </list>
/// 字段：<c>command</c>+<c>args</c>+<c>env</c>（stdio）或 <c>url</c>+<c>headers</c>（HTTP）。
/// 也接受 <c>arguments</c> 作为 <c>args</c> 的别名。解析失败的文件跳过。
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
                AddFromElement(doc.RootElement, list);
            }
            catch
            {
                /* 解析失败的配置文件跳过，不阻断启动 */
            }
        }
        return list;
    }

    private static void AddFromElement(JsonElement el, List<McpServerConfig> list)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) list.Add(ParseConfig(item, null));
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            // 主流 mcpServers 映射：{ "mcpServers": { "name": { ... } } }
            if (el.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in map.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        list.Add(ParseConfig(prop.Value, prop.Name));
            }
            else
            {
                list.Add(ParseConfig(el, null));
            }
        }
    }

    private static McpServerConfig ParseConfig(JsonElement el, string? name)
    {
        string? Get(string n) =>
            el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        // args 优先（主流），arguments 作为别名
        IReadOnlyList<string>? args = null;
        if (el.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();
        else if (el.TryGetProperty("arguments", out a) && a.ValueKind == JsonValueKind.Array)
            args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();

        return new McpServerConfig
        {
            Name = name ?? Get("name") ?? string.Empty,
            Command = Get("command"),
            Arguments = args,
            WorkingDirectory = Get("workingDirectory") ?? Get("cwd"),
            Env = ParseStringMap(el, "env"),
            Url = Get("url"),
            Headers = ParseStringMap(el, "headers"),
        };
    }

    private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in p.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? "";
        return dict.Count == 0 ? null : dict;
    }
}


