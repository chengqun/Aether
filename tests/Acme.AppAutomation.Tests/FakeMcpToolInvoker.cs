using System.Collections.Generic;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Tests;

/// <summary>假 <see cref="IMcpToolInvoker"/>：记录工具调用，返回可控 JSON。测 <c>McpDesktopAutomation</c> 的原语↔工具映射。</summary>
public sealed class FakeMcpToolInvoker : IMcpToolInvoker
{
    public bool IsConnected { get; set; } = true;
    public List<(string Tool, object? Args)> Calls { get; } = new();
    public string ReturnJson { get; set; } = "{}";

    public Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default)
    {
        Calls.Add((toolName, args));
        return Task.FromResult(ReturnJson);
    }
}
