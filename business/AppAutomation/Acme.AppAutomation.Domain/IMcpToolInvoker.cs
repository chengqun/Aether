namespace Acme.AppAutomation.Domain;

/// <summary>
/// MCP 工具调用端口（线客户端 seam）。<see cref="IDesktopAutomation"/> 的 MCP 实现（<c>McpDesktopAutomation</c>）
/// 经此端口调 MCP server 暴露的 Click/Type/GetUITree 等工具。
/// <para>真实实现（包 <c>McpClient</c>）在宿主侧注册（MCP alpha 包隔离在 <c>Aether.AI.Mcp</c>，不污染本 context）；
/// 测试用 fake。这样 <c>McpDesktopAutomation</c> 的原语↔工具映射逻辑可单测，无需真 server。</para>
/// </summary>
public interface IMcpToolInvoker
{
    bool IsConnected { get; }

    /// <summary>按名调 MCP 工具，返回 JSON 字符串结果。</summary>
    Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default);
}
