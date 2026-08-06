using System.Text.Json;
using Acme.AppAutomation.Domain;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Acme.AppAutomation.McpClientHost;

/// <summary>
/// <see cref="IMcpToolInvoker"/> 的复用实现：不自己连 server，而是复用 <see cref="IMcpServerRegistry"/> 已连好的
/// <c>desktop</c> server 的工具（<see cref="AIFunction"/>），按工具名找 <see cref="AIFunction"/> 调 <see cref="AIFunction.InvokeAsync"/>。
///
/// <para><b>为什么复用而不是独立连</b>：<see cref="McpServerRegistry"/> 在 <c>MainViewModelBase.InitializeAsync</c>
/// 后台 <c>StartAsync</c> 已连好所有配置的 server（含 desktop），专家页 MCP tab 走的就是它。股票页的固定过程路径
/// 若独立连，既重复连同一 server，又有惰性连接没人触发 <c>EnsureConnected</c> → <see cref="IsConnected"/> 恒 false、
/// <c>ProcedureRunner</c> fail-fast 的问题。复用 registry：连接已建立、<see cref="IsConnected"/> 真实反映 desktop server 状态。</para>
///
/// <para>工具查找：从 <see cref="IMcpServerRegistry.GetTools"/> 按精确名找（desktop server 工具为 <c>windows_*</c> 前缀）。
/// 结果：<see cref="AIFunction.InvokeAsync"/> 返回 <c>TextContent</c>（<see cref="object.ToString"/> 给文本），本类转成字符串。</para>
///
/// <para>MCP alpha 包隔离在 <c>Acme.AppAutomation.McpClient</c>；本类只引 <see cref="IMcpServerRegistry"/>（Hosting 接口，无 alpha）
/// + <c>Microsoft.Extensions.AI</c>（<see cref="AIFunction"/>，稳定包）。</para>
/// </summary>
public sealed class RegistryMcpToolInvoker : IMcpToolInvoker
{
    private readonly IMcpServerRegistry _registry;
    private readonly ILogger<RegistryMcpToolInvoker>? _log;

    /// <summary>复用的 desktop server 名（<c>mcp-servers/desktop.json</c> 里 server 名必须是 <c>desktop</c>）。</summary>
    public const string DesktopServerName = "desktop";

    public RegistryMcpToolInvoker(IMcpServerRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = loggerFactory?.CreateLogger<RegistryMcpToolInvoker>();
    }

    /// <summary>
    /// desktop server 是否可用。只要 registry 配置了 desktop server（不论是否已连上）即视为 true——
    /// 真正的连接在 <see cref="CallToolAsync"/> 里 <c>await StartAsync</c> 兜底，错误在那里具体报。
    /// <para>这样避免 <c>ProcedureRunner</c> 在 registry 后台连接完成前误判 fail-fast（连接是 InitializeAsync 异步发起的）。</para>
    /// <para>未配 desktop server（Mac/未配置）→ registry 里无 desktop 条目 → false，操作优雅失败。</para>
    /// </summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                foreach (var s in _registry.GetServers())
                    if (string.Equals(s.Config.Name, DesktopServerName, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { /* registry 未就绪时返回 false */ }
            return false;
        }
    }

    public async Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default)
    {
        // 确保 registry 已启动（InitializeAsync 后台已调，这里兜底：未启动则等一下，避免股票页先于启动完成点连接）。
        try { await _registry.StartAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { return ErrorJson($"MCP server 未就绪：{ex.Message}"); }

        AIFunction? tool = null;
        foreach (var t in _registry.GetTools())
            if (t.Name == toolName) { tool = t; break; }
        if (tool is null)
            return ErrorJson($"MCP 工具不存在：{toolName}（desktop server 未连或工具名不匹配）");

        try
        {
            var funcArgs = MapArgs(args);
            var result = await tool.InvokeAsync(funcArgs, ct).ConfigureAwait(false);
            return ResultToText(result);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "调 MCP 工具 {Tool} 失败。", toolName);
            return ErrorJson($"工具 {toolName} 调用异常：{ex.Message}");
        }
    }

    /// <summary>把 args（匿名对象 / Dictionary / JSON 串 / null）转成 <see cref="AIFunctionArguments"/>。</summary>
    private static AIFunctionArguments MapArgs(object? args)
    {
        var fa = new AIFunctionArguments();
        if (args is null) return fa;

        JsonElement element;
        if (args is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return fa;
            using var doc = JsonDocument.Parse(s);
            element = doc.RootElement.Clone();
        }
        else
        {
            element = JsonSerializer.SerializeToElement(args);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
                fa[prop.Name] = prop.Value.Clone();
        }
        return fa;
    }

    /// <summary>把 <see cref="AIFunction.InvokeAsync"/> 的返回（常是 <c>TextContent</c>）转成文本。</summary>
    private static string ResultToText(object? result)
    {
        if (result is null) return "";
        // TextContent.ToString() 返回 Text 字段；其它 AIContent 同理。直接 ToString 兜底最稳。
        return result.ToString() ?? "";
    }

    private static string ErrorJson(string msg)
        => "{\"error\":" + JsonSerializer.Serialize(msg) + "}";
}
