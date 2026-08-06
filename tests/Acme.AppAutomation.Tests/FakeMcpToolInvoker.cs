using System.Collections.Generic;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Tests;

/// <summary>
/// 假 <see cref="IMcpToolInvoker"/>：记录工具调用，按工具名返回可控 JSON/文本。测 <c>McpDesktopAutomation</c> 的
/// locator↔FlaUI-MCP 工具映射（snapshot 找 ref → click ref）。<see cref="ReturnJson"/> 为默认返回；
/// <see cref="ReturnsByTool"/> 按工具名覆盖（如 snapshot 返回树、get_text 返回文本）。
/// </summary>
public sealed class FakeMcpToolInvoker : IMcpToolInvoker
{
    public bool IsConnected { get; set; } = true;
    public List<(string Tool, object? Args)> Calls { get; } = new();
    public string ReturnJson { get; set; } = "{}";
    /// <summary>按工具名覆盖返回值（优先于 <see cref="ReturnJson"/>）。</summary>
    public Dictionary<string, string> ReturnsByTool { get; } = new();

    public Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default)
    {
        Calls.Add((toolName, args));
        var result = ReturnsByTool.TryGetValue(toolName, out var specific) ? specific : ReturnJson;
        return Task.FromResult(result);
    }
}
