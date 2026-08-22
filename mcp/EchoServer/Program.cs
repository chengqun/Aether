using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ── 示例 MCP server：EchoServer ──────────────────────────────────────────────
// 与 SampleServer 同一套路：用 ModelContextProtocol server SDK 经 stdio 暴露工具，
// 供 Aether agent 调用。mcp/ 下一个独立项目（自带 csproj），不引用 Aether。
//
// 接入方式：用 mcp-add-server 写配置指向此项目发布产物：
//   { "mcpServers": { "echo": { "command": "<仓库绝对路径>/mcp/EchoServer/publish/EchoServer" } } }
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// stdio 传输要求 stdout 专用于 JSON-RPC 协议流——把日志全部重定向到 stderr，防止 info 日志污染协议流。
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();

/// <summary>示例工具集 2：用 [McpServerTool] 把方法暴露给 agent。</summary>
[McpServerToolType]
public static class EchoTools
{
    [McpServerTool, Description("把两个整数相乘并返回结果。")]
    public static int Multiply(int a, int b) => a * b;

    [McpServerTool, Description("回显输入文本；可选转大写、可选附加前缀。")]
    public static string Echo(string text, bool uppercase = false, string? prefix = null)
    {
        var body = uppercase ? text.ToUpperInvariant() : text;
        return prefix is null ? body : $"{prefix}{body}";
    }
}
