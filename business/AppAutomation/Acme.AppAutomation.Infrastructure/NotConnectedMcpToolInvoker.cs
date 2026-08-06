using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// <see cref="IMcpToolInvoker"/> 的「未连接」桩：无 MCP server 时用（如 macOS 未配 desktop server）。
/// IsConnected 恒 false，调任何工具返回空 JSON。<see cref="ProcedureRunner"/> 会因 IsConnected=false
/// 直接返回「桌面控制后端未连接」，操作优雅失败而非崩。
/// <para>Windows 期换成真实实现（包 <c>McpClient</c> 连 FlaUI-MCP）替换本注册即可。</para>
/// </summary>
public sealed class NotConnectedMcpToolInvoker : IMcpToolInvoker
{
    public bool IsConnected => false;
    public Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default)
        => Task.FromResult("{}");
}
