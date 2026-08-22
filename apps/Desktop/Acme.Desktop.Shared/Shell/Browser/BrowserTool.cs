using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>浏览器网络面板 agent 工具组（宿主层，镜像 <see cref="RunInTerminalTool"/>/<see cref="BuildTool"/> 范式——需 <see cref="IBrowserService"/>，无业务上下文）。
/// agent 工作流：<see cref="BrowserOpenAsync"/> 打开浏览器到登录页（用户手动登录）→ <see cref="GetCookiesAsync"/> 拿 curl 直接用的 CookieHeader →
/// ExecuteShellCommand/run_in_terminal 跑 <c>curl -s -H "Cookie: {CookieHeader}" https://同域/任意路径</c> 访问需登录接口。
/// 与用户面板共用同一 <see cref="IBrowserService"/> 单例：agent 取活跃会话、无则建新（用户可见，同 run_in_terminal）。</summary>
public sealed class BrowserTool
{
    private readonly IBrowserService _service;
    public BrowserTool(IBrowserService service) => _service = service;

    /// <summary>打开浏览器到指定 URL（headed 独立 OS 窗口，用户手动登录）。有活跃会话则导航它，否则建新。
    /// channel=chrome/msedge 复用本机已装浏览器（免 playwright install chromium）。</summary>
    public async Task<BrowserOpenOutput> BrowserOpenAsync(
        [Description("要打开的登录页或网站 URL")] string url,
        [Description("浏览器通道：chrome/msedge 复用本机浏览器（免 playwright install chromium）；不传用 Playwright 自带 chromium")] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _service.EnsureOpenAsync(url, channel, cancellationToken);
        return new BrowserOpenOutput
        {
            Success = session.IsAlive,
            SessionId = session.Id,
            Url = session.Url ?? url,
            Title = session.Title,
            IsAlive = session.IsAlive,
            Error = session.IsAlive ? null : "浏览器打开失败。若未安装 Chromium，执行 playwright install chromium，或传 channel=chrome 复用本机 Chrome。",
        };
    }

    /// <summary>提取当前活跃浏览器会话的 cookie（Playwright 按 host 过滤，含父子域 + HttpOnly）。
    /// 返回 curl 直接用的 <see cref="BrowserCookieResult.CookieHeader"/>（name=value; name=value; ...）+ 明细。
    /// ⚠ cookie 是登录凭证，会进 agent 上下文供后续 curl 用。</summary>
    public async Task<BrowserCookieResult> GetCookiesAsync(
        [Description("只返回指定名字的 cookie（过滤 analytics/csrf 噪音）。不传则返回该 URL 适用的所有 cookie")] List<string>? cookieNames = null,
        CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return new BrowserCookieResult { Success = false, Error = "无活跃浏览器会话。先用 browser_open 打开登录页并完成登录。" };
        return await _service.GetCookiesAsync(activeId.Value, cookieNames, cancellationToken);
    }

    /// <summary>取当前活跃浏览器会话已记录的 HTTP 请求列表（DevTools Network 风格）。
    /// 用于发现登录后调用的 API 端点与其状态，或确认登录是否成功（登录接口 200 + Set-Cookie）。</summary>
    public Task<BrowserRequestsOutput> GetRequestsAsync(
        [Description("只返回 URL 含此片段的请求（大小写不敏感）。不传则全量")] string? urlContains = null,
        [Description("限制返回条数（默认 50）。agent 可传大值取全量")] int? limit = 50,
        [Description("只看失败/4xx/5xx 请求")] bool errorsOnly = false,
        CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return Task.FromResult(new BrowserRequestsOutput { Success = false, Error = "无活跃浏览器会话。" });
        var reqs = _service.GetRequests(activeId.Value);
        IEnumerable<NetworkRequestItem> filtered = reqs;
        if (!string.IsNullOrEmpty(urlContains))
            filtered = filtered.Where(r => r.Url.Contains(urlContains, System.StringComparison.OrdinalIgnoreCase));
        if (errorsOnly) filtered = filtered.Where(r => r.IsError);
        var limited = limit is { } l && l > 0 ? filtered.Take(l) : filtered;
        var summaries = limited.Select(r => new BrowserRequestSummary
        {
            Method = r.Method,
            Url = r.Url,
            Status = r.Status,
            StatusText = r.StatusText,
            DurationMs = r.DurationMs,
            ResourceType = r.ResourceType,
            IsError = r.IsError,
        }).ToList();
        return Task.FromResult(new BrowserRequestsOutput { Success = true, Count = summaries.Count, Requests = summaries });
    }

    /// <summary>关闭当前活跃浏览器会话（关浏览器 + 释放 Playwright 资源）。</summary>
    public Task<BrowserCloseOutput> CloseAsync(CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return Task.FromResult(new BrowserCloseOutput { Success = false, Error = "无活跃浏览器会话。" });
        _service.CloseSession(activeId.Value);
        return Task.FromResult(new BrowserCloseOutput { Success = true });
    }
}

public sealed class BrowserOpenOutput
{
    public bool Success { get; set; }
    public Guid SessionId { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsAlive { get; set; }
    public string? Error { get; set; }
}

public sealed class BrowserRequestsOutput
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public List<BrowserRequestSummary> Requests { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class BrowserRequestSummary
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? Status { get; set; }
    public string? StatusText { get; set; }
    public int? DurationMs { get; set; }
    public string? ResourceType { get; set; }
    public bool IsError { get; set; }
}

public sealed class BrowserCloseOutput
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
