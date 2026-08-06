using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// <see cref="IDesktopAutomation"/> 的 MCP 实现：把上层 locator 抽象翻译成 FlaUI-MCP server 的
/// <c>windows_*</c> 工具 + ref 模型（经 <see cref="IMcpToolInvoker"/> seam 调）。
///
/// <para><b>FlaUI-MCP 的模型是 ref 驱动</b>（仿 Playwright）：先 <c>windows_snapshot</c> 取 accessibility 树
/// （每行 <c>- role "name" [ref=wXeY]</c>），再用 ref 调 <c>windows_click</c>/<c>windows_type</c>/<c>windows_get_text</c>。
/// 本类把 <see cref="DesktopLocator"/>（ByName/ByRole）映射成「snapshot → 在树里按 Name/Role 找 ref → 调对应工具」。</para>
///
/// <para>不直接依赖 <c>McpClient</c>——经 <see cref="IMcpToolInvoker"/> seam，隔离 MCP alpha 包，使映射逻辑可单测。
/// 真实 <c>IMcpToolInvoker</c>（包 McpClient 连 FlaUI-MCP）在 <c>Acme.AppAutomation.McpClient</c>，宿主侧注册。</para>
///
/// <para>工具名与参数对齐 FlaUI-MCP（https://github.com/shanselman/FlaUI-MCP）：
/// windows_focus{title} / windows_list_windows / windows_snapshot{handle?} / windows_click{ref,button?,doubleClick?}
/// / windows_type{ref?,text} / windows_send_keys{ref?,chord?|keys?} / windows_get_text{ref} / windows_launch{app}。</para>
/// </summary>
public sealed class McpDesktopAutomation : IDesktopAutomation
{
    private readonly IMcpToolInvoker _invoker;

    // FlaUI-MCP 工具名（与 server 端 ToolRegistry.Name 对齐）。
    private const string ToolFocus = "windows_focus";
    private const string ToolListWindows = "windows_list_windows";
    private const string ToolSnapshot = "windows_snapshot";
    private const string ToolClick = "windows_click";
    private const string ToolDoubleClick = "windows_click";   // 同工具，doubleClick=true
    private const string ToolRightClick = "windows_click";    // 同工具，button=right
    private const string ToolType = "windows_type";
    private const string ToolSendKeys = "windows_send_keys";
    private const string ToolGetText = "windows_get_text";
    private const string ToolLaunch = "windows_launch";

    public McpDesktopAutomation(IMcpToolInvoker invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public bool IsConnected => _invoker.IsConnected;

    /// <summary>
    /// 当前活动窗口的 server 端 handle（<c>wN</c>）。FlaUI-MCP 的 handle 是 server 进程内自增计数器，
    /// <b>不是</b> OS 句柄；<see cref="FocusWindowAsync"/> 经 <c>windows_focus</c> 拿到后存此，
    /// 后续 <c>windows_snapshot</c> 带上它才能定位到正确窗口（不带 handle 会取「最近 launched」窗口，常错）。
    /// </summary>
    private string? _activeHandle;

    // ─────────────────────────── 窗口 ───────────────────────────

    public async Task FocusWindowAsync(string titleOrId, CancellationToken ct = default)
    {
        var json = await _invoker.CallToolAsync(ToolFocus, new { title = titleOrId }, ct).ConfigureAwait(false);
        // windows_focus 返回文本：Focused window "标题" (handle: w5)。解析出 handle 存起来供后续 snapshot 用。
        _activeHandle = ExtractHandle(ExtractText(json)) ?? _activeHandle;
    }

    public async Task<bool> WindowExistsAsync(string titleOrId, CancellationToken ct = default)
    {
        var json = await _invoker.CallToolAsync(ToolListWindows, null, ct).ConfigureAwait(false);
        var text = ExtractText(json);
        // list_windows 输出：- wN: "title" (process)。按标题包含匹配。
        return text.Contains(titleOrId, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── 点击（snapshot→找 ref→click）───────────────────────────

    public async Task ClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => await ClickCoreAsync(locator, button: "left", doubleClick: false, ct).ConfigureAwait(false);

    public async Task DoubleClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => await ClickCoreAsync(locator, button: "left", doubleClick: true, ct).ConfigureAwait(false);

    public async Task RightClickAsync(DesktopLocator locator, CancellationToken ct = default)
        => await ClickCoreAsync(locator, button: "right", doubleClick: false, ct).ConfigureAwait(false);

    private async Task ClickCoreAsync(DesktopLocator locator, string button, bool doubleClick, CancellationToken ct)
    {
        var refId = await ResolveRefAsync(locator, ct).ConfigureAwait(false);
        if (refId is null)
            throw new InvalidOperationException($"找不到元素定位 {locator}（snapshot 树中无匹配，可能 UI 已变化或窗口未就绪）");

        await _invoker.CallToolAsync(ToolClick, new { @ref = refId, button, doubleClick }, ct).ConfigureAwait(false);
    }

    // ─────────────────────────── 输入 ───────────────────────────

    public Task TypeAsync(string text, CancellationToken ct = default)
        => _invoker.CallToolAsync(ToolType, new { text }, ct);

    public Task KeyAsync(string combo, CancellationToken ct = default)
        => _invoker.CallToolAsync(ToolSendKeys, new { chord = ToChord(combo) }, ct);

    // ─────────────────────────── 读取 ───────────────────────────

    public async Task<DesktopElement?> FindElementAsync(DesktopLocator locator, CancellationToken ct = default)
    {
        var refId = await ResolveRefAsync(locator, ct).ConfigureAwait(false);
        return refId is null ? null : new DesktopElement { Id = refId };
    }

    public async Task<string?> GetTextAsync(DesktopLocator locator, CancellationToken ct = default)
    {
        var refId = await ResolveRefAsync(locator, ct).ConfigureAwait(false);
        if (refId is null) return null;
        var json = await _invoker.CallToolAsync(ToolGetText, new { @ref = refId }, ct).ConfigureAwait(false);
        return ExtractText(json);
    }

    public async Task<string> GetUITreeAsync(string? scope = null, CancellationToken ct = default)
    {
        // scope 在 FlaUI-MCP 里是 window handle（windows_snapshot 的 handle 参数）。
        // 未显式传 scope 时用 _activeHandle（FocusWindowAsync 记下的）；都没有才让 server 取默认（最近 launched）。
        var handle = scope ?? _activeHandle;
        var args = handle is null ? null : new { handle };
        var json = await _invoker.CallToolAsync(ToolSnapshot, args, ct).ConfigureAwait(false);
        return ExtractText(json);
    }

    public async Task<bool> WaitForElementAsync(DesktopLocator locator, int timeoutMs = 5000, CancellationToken ct = default)
    {
        // FlaUI-MCP 无内置 wait 工具；轮询 snapshot 找元素，超时返回 false。
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var refId = await ResolveRefAsync(locator, ct).ConfigureAwait(false);
            if (refId is not null) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public Task SleepAsync(int ms, CancellationToken ct = default)
        => Task.Delay(ms, ct);   // 本地 sleep，不走 server

    // ─────────────────────────── ref 解析（snapshot 树里按 locator 匹配）───────────────────────────

    /// <summary>
    /// 取窗口 snapshot 文本，在其中按 <paramref name="locator"/> 找匹配元素的 ref。
    /// snapshot 行格式：<c>- role "name" [ref=wXeY] [state...]</c>（缩进表示层级）。
    /// </summary>
    private async Task<string?> ResolveRefAsync(DesktopLocator locator, CancellationToken ct)
    {
        var tree = await GetUITreeAsync(scope: null, ct).ConfigureAwait(false);
        return FindRefInTree(tree, locator);
    }

    /// <summary>在 snapshot 树文本里按 locator 匹配元素，返回 ref（wXeY）。找不到返回 null。</summary>
    public static string? FindRefInTree(string tree, DesktopLocator locator)
    {
        if (string.IsNullOrEmpty(tree)) return null;

        // 逐行匹配：- role "name" [ref=...]  或  - role [ref=...]（无 name）
        // role 取行首第一个词（到空格或 " ）；name 取第一个双引号串；ref 取 [ref=...]。
        foreach (var line in tree.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('-')) continue;

            var refMatch = RefPattern.Match(trimmed);
            if (!refMatch.Success) continue;
            var refId = refMatch.Groups[1].Value;

            var nameMatch = NamePattern.Match(trimmed);
            var roleMatch = RolePattern.Match(trimmed);

            bool hit = locator.Kind switch
            {
                LocatorKind.Name => nameMatch.Success && nameMatch.Groups[1].Value == locator.Value,
                LocatorKind.Role => roleMatch.Success && roleMatch.Groups[1].Value.Equals(locator.Value, StringComparison.OrdinalIgnoreCase),
                // AutomationId/Coordinates 在 FlaUI-MCP snapshot 里无直接体现，回退为 name 匹配。
                LocatorKind.AutomationId => nameMatch.Success && nameMatch.Groups[1].Value == locator.Value,
                LocatorKind.Coordinates => false,
                _ => false
            };
            if (hit) return refId;
        }
        return null;
    }

    // 行解析正则：ref=wXeY；"name"；行首 role（- 之后第一个词）。
    private static readonly Regex RefPattern = new(@"\[ref=([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex RolePattern = new(@"^-\s+(\S+)", RegexOptions.Compiled);
    // windows_focus 返回：Focused window "标题" (handle: w5)
    private static readonly Regex HandlePattern = new(@"handle:\s*(w\d+)", RegexOptions.Compiled);

    /// <summary>从 windows_focus 返回文本里提取 handle（wN）。</summary>
    internal static string? ExtractHandle(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = HandlePattern.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 把上层 combo（小写 "ctrl+a"/"tab"/"enter"）转成 FlaUI-MCP <c>send_keys</c> 的 chord 格式（"Ctrl+A"/"Tab"/"Enter"）。
    /// FlaUI-MCP 用 Playwright 风格 chord：键名首字母大写、+ 分隔。
    /// </summary>
    public static string ToChord(string combo)
    {
        if (string.IsNullOrEmpty(combo)) return "";
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('+');
            sb.Append(NormalizeKey(parts[i].Trim()));
        }
        return sb.ToString();
    }

    private static string NormalizeKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return k;
        // 单字符（含 shift 符号）原样；命名键首字母大写。
        if (k.Length == 1) return k.ToUpperInvariant();
        return char.ToUpperInvariant(k[0]) + k[1..].ToLowerInvariant();
    }

    // —— 极简 JSON 取值：FlaUI-MCP 工具结果经 McpToolInvoker 返回的是 { content: [{type:"text",text:"..."}] } 或裸文本 ——
    // McpDesktopToolInvoker 已把 TextContentBlock 拼成字符串返回；这里再兜一层（兼容 fake invoker 的自定义 JSON）。

    private static string ExtractText(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        // 优先按 McpDesktopToolInvoker 的输出（已是纯文本拼接）直接返回。
        var trimmed = json.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return json;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            // { "content": [ { "text": "..." } ] } 形态
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in content.EnumerateArray())
                    if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                return sb.ToString();
            }
            // { "text": "..." } / { "result": "..." } 形态
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
                if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString() ?? "";
            }
            return trimmed;
        }
        catch
        {
            return json;
        }
    }
}
