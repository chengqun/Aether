using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

// ── 示例 MCP server ──────────────────────────────────────────────────────────
// 用 ModelContextProtocol server SDK 经 stdio 暴露工具，供 Aether agent 调用。
// 每个 MCP server 是 mcp/ 下的一个独立项目（自带 csproj），互不依赖、不引用 Aether。
//
// 启用方式：在租户数据目录的 mcp-servers/ 下放一个 JSON 配置，指向此项目：
//   { "name": "sample", "command": "dotnet", "arguments": ["run", "--project", "<仓库绝对路径>/mcp/SampleServer"] }
// 远程 HTTP server 则用：{ "name": "remote", "url": "https://host/sse" }
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();

/// <summary>示例工具集：用 [McpServerTool] 把方法暴露给 agent。</summary>
[McpServerToolType]
public static class SampleTools
{
    [McpServerTool, Description("把两个整数相加并返回结果。")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool, Description("返回当前服务器本地时间（ISO 8601）。")]
    public static string Now() => DateTimeOffset.Now.ToString("O");
}
