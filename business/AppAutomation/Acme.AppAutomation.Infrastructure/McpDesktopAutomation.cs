using System.Globalization;
using System.Text.Json;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// <see cref="IDesktopAutomation"/> 的 MCP 实现：把每个原语映射成对桌面控制 MCP server 的工具调用
/// （经 <see cref="IMcpToolInvoker"/>）。工具名对齐 FlaUI-MCP 风格（click/type/screenshot/get_ui_tree…）。
/// <para>不直接依赖 <c>McpClient</c>——经 <see cref="IMcpToolInvoker"/> seam，隔离 MCP alpha 包，
/// 使本类（原语↔工具映射）可单测。真实 <c>IMcpToolInvoker</c>（包 McpClient）在宿主侧注册。</para>
/// <para>Windows 期：连 FlaUI-MCP；Mac 期：连自建 Mac-MCP。两端工具名一致即可复用本类。</para>
/// </summary>
public sealed class McpDesktopAutomation : IDesktopAutomation
{
    private readonly IMcpToolInvoker _invoker;

    public McpDesktopAutomation(IMcpToolInvoker invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public bool IsConnected => _invoker.IsConnected;

    public Task FocusWindowAsync(string titleOrId, CancellationToken ct = default)
        => _invoker.CallToolAsync("focus_window", new { title = titleOrId }, ct);

    public async Task<bool> WindowExistsAsync(string titleOrId, CancellationToken ct = default)
    {
        var json = await _invoker.CallToolAsync("window_exists", new { title = titleOrId }, ct).ConfigureAwait(false);
        return JsonBool(json);
    }

    public Task ClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => _invoker.CallToolAsync("click", locator.ToArgs(), ct);

    public Task DoubleClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => _invoker.CallToolAsync("double_click", locator.ToArgs(), ct);

    public Task RightClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => _invoker.CallToolAsync("right_click", locator.ToArgs(), ct);

    public Task TypeAsync(string text, CancellationToken ct = default)
        => _invoker.CallToolAsync("type", new { text }, ct);

    public Task KeyAsync(string combo, CancellationToken ct = default)
        => _invoker.CallToolAsync("key", new { combo }, ct);

    public async Task<DesktopElement?> FindElementAsync(DesktopLocator locator, CancellationToken ct = default)
    {
        var json = await _invoker.CallToolAsync("find_element", locator.ToArgs(), ct).ConfigureAwait(false);
        return ParseElement(json);
    }

    public async Task<string?> GetTextAsync(DesktopLocator locator, CancellationToken ct = default)
    {
        var json = await _invoker.CallToolAsync("get_text", locator.ToArgs(), ct).ConfigureAwait(false);
        return JsonString(json, "text");
    }

    public Task<string> GetUITreeAsync(string? scope = null, CancellationToken ct = default)
        => _invoker.CallToolAsync("get_ui_tree", scope is null ? null : new { scope }, ct);

    public async Task<bool> WaitForElementAsync(DesktopLocator locator, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var args = locator.ToArgs();
        args["timeout_ms"] = timeoutMs;
        var json = await _invoker.CallToolAsync("wait_for_element", args, ct).ConfigureAwait(false);
        return JsonBool(json);
    }

    public Task SleepAsync(int ms, CancellationToken ct = default)
        => _invoker.CallToolAsync("sleep", new { ms }, ct);

    // —— 极简 JSON 取值（避免依赖具体 MCP server 的返回 schema；Windows 期对接 FlaUI-MCP 时按实际 schema 调整）——

    private static bool JsonBool(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("result", out var r) && r.GetBoolean();
    }

    private static string? JsonString(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
    }

    private static DesktopElement? ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var o = doc.RootElement;
        return new DesktopElement
        {
            Id = o.TryGetProperty("id", out var id) ? id.GetString() : null,
            Name = o.TryGetProperty("name", out var n) ? n.GetString() : null,
            Role = o.TryGetProperty("role", out var role) ? role.GetString() : null,
            Text = o.TryGetProperty("text", out var t) ? t.GetString() : null,
        };
    }
}
