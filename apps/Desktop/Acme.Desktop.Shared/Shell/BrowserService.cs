using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Aether.AI.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Acme.Desktop.Shared.Shell;

/// <summary>浏览器网络面板服务（impl <see cref="IBrowserService"/>，镜像 <see cref="TerminalService"/> 范式）。
/// 持久 Playwright 会话：每个会话一个 headed Chromium 独立 OS 窗口（用户手动登录），app 内面板只展示该会话的 HTTP 请求流。
/// 用户面板与 agent 工具共用同一 service 实例（agent browser_open 取活跃会话、无则建新——用户可见，同 run_in_terminal）。
///
/// 线程模型：Playwright .NET 无线程亲和硬约束（CloudDocsAuthService 单 async 链跨 ThreadPool 跑通），
/// 但同一 page/context 的并发操作会竞争——故 per-session <see cref="BrowserSession.OpLock"/> 串行化所有主动操作（Navigate/GetCookies/Close）。
/// Playwright 的 Request/Response 事件在驱动回调线程触发，经 <see cref="BrowserSession._stateLock"/> 保护请求字典/列表的增改；
/// <see cref="NetworkRequestItem"/> 的字段更新经 Avalonia 绑定引擎自动 marshal 到 UI 线程。请求通知经 per-session Timer 去抖（150ms）合批，避免页面加载几十个请求刷爆 UI。</summary>
public sealed class BrowserService : IBrowserService
{
    private readonly Dictionary<Guid, BrowserSession> _sessions = new();
    private readonly object _lock = new();
    private readonly string? _profileDir;
    private Guid? _activeId;

    /// <param name="options">读 <see cref="AetherAIOptions.BrowserProfileDir"/>——非空则持久 profile（登录一次后续复用），null 退回临时 profile（兼容旧行为）。</param>
    public BrowserService(IOptions<AetherAIOptions> options) => _profileDir = options.Value.BrowserProfileDir;

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? RequestsChanged;
    public event Action<Guid>? SessionClosed;

    public Guid? ActiveSessionId
    {
        get { lock (_lock) return _activeId; }
    }

    public IBrowserSession OpenSession(string url, string? channel = null)
    {
        var id = Guid.NewGuid();
        var session = new BrowserSession(id, this, _profileDir);
        lock (_lock) _sessions[id] = session;
        _activeId = id;
        // Playwright 启动在后台跑（不阻塞 UI 线程 / agent 工具返回）；建好/失败都经事件通知 VM。
        session.InitializeAsync(url, channel).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Trace.WriteLine($"browser session {id} init failed: {t.Exception}");
                session.MarkDead();
                SessionClosed?.Invoke(id);
            }
            SessionsChanged?.Invoke();
            ActiveSessionChanged?.Invoke(id);
        }, TaskScheduler.Default);
        // 立即返回句柄（IsAlive=true，Title 占位）；真启动在后台。
        return session;
    }

    public void CloseSession(Guid sessionId)
    {
        BrowserSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out session)) return;
            _sessions.Remove(sessionId);
            if (_activeId == sessionId) _activeId = _sessions.Keys.LastOrDefault();
        }
        session.MarkDead();
        SessionClosed?.Invoke(sessionId);
        SessionsChanged?.Invoke();
        // 实际关浏览器在后台跑（不阻塞 UI 线程）。
        session.DisposeAsync().AsTask().SafeFireAndForget();
        if (_activeId is { } newActive) ActiveSessionChanged?.Invoke(newActive);
    }

    public void SetActive(Guid sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId)) return;
            _activeId = sessionId;
        }
        ActiveSessionChanged?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public IReadOnlyList<IBrowserSession> GetSessions()
    {
        lock (_lock) return _sessions.Values.Select(s => (IBrowserSession)s).ToList();
    }

    public Task NavigateAsync(Guid sessionId, string url)
    {
        BrowserSession? s;
        lock (_lock) { if (!_sessions.TryGetValue(sessionId, out s)) return Task.CompletedTask; }
        return s.NavigateAsync(url);
    }

    public async Task<IBrowserSession> EnsureOpenAsync(string url, string? channel, CancellationToken ct)
    {
        BrowserSession? active = null;
        lock (_lock) { if (_activeId is { } aid && _sessions.TryGetValue(aid, out var a) && a.IsAlive) active = a; }
        if (active is not null) { await active.NavigateAsync(url); return active; }
        return await OpenSessionAsync(url, channel, ct);
    }

    /// <summary>建新会话并等 Playwright 就绪（agent 工具路径）。失败也返回会话句柄（IsAlive=false）。</summary>
    public async Task<IBrowserSession> OpenSessionAsync(string url, string? channel, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var session = new BrowserSession(id, this, _profileDir);
        lock (_lock) { _sessions[id] = session; _activeId = id; }
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(id);
        try { await session.InitializeAsync(url, channel, ct); }
        catch (OperationCanceledException) { session.MarkDead(); SessionClosed?.Invoke(id); throw; }
        catch (Exception ex)
        {
            Trace.WriteLine($"browser session {id} init failed: {ex}");
            session.MarkDead();
            SessionClosed?.Invoke(id);
            SessionsChanged?.Invoke();
        }
        return session;
    }

    public IReadOnlyList<NetworkRequestItem> GetRequests(Guid sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var s) ? s.GetRequestsSnapshot() : Array.Empty<NetworkRequestItem>();
        }
    }

    public void ClearRequests(Guid sessionId) =>
        WithSession(sessionId, session => { session.ClearRequests(); RequestsChanged?.Invoke(sessionId); });

    public Task<BrowserCookieResult> GetCookiesAsync(Guid sessionId, List<string>? cookieNames = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return Task.FromResult(new BrowserCookieResult { Success = false, Error = "会话不存在" });
            if (!s.IsAlive)
                return Task.FromResult(new BrowserCookieResult { Success = false, Error = "会话已关闭" });
            return s.GetCookiesAsync(cookieNames, cancellationToken);
        }
    }

    private void WithSession(Guid sessionId, Action<BrowserSession> action)
    {
        BrowserSession? s;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out s)) return;
        }
        action(s);
    }

    // —— BrowserSession 内部回调（事件触发时经此通知 service → VM）——
    internal void RaiseRequestsChanged(Guid id) => RequestsChanged?.Invoke(id);
    internal void RaiseSessionClosed(Guid id) { SessionClosed?.Invoke(id); SessionsChanged?.Invoke(); }
    internal void RaiseSessionsChanged() => SessionsChanged?.Invoke();
}

/// <summary>单个浏览器会话（含 Playwright 资源 + 请求记录）。实现 <see cref="IBrowserSession"/> 供 VM 投影元数据。</summary>
internal sealed class BrowserSession : IBrowserSession, IAsyncDisposable
{
    private readonly BrowserService _owner;
    private readonly SemaphoreSlim _opLock = new(1, 1);   // 串行化主动操作（防 Playwright 对象并发竞争）
    private readonly object _stateLock = new();           // 保护请求字典/列表的增改（事件来自 Playwright 线程）
    private readonly Dictionary<IRequest, NetworkRequestItem> _byRequest = new();
    private readonly List<NetworkRequestItem> _requests = new();
    private readonly Timer _requestsDebounce;

    private IPlaywright? _playwright;
    private IBrowser? _browser;          // 临时模式用；持久模式（LaunchPersistentContextAsync）无独立 IBrowser 句柄
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly string? _userDataDir;  // 持久 profile 目录；非空则 LaunchPersistentContextAsync（登录一次复用），null 退回临时

    private volatile string _url;
    private volatile string _title;
    private volatile bool _isAlive = true;

    public Guid Id { get; }
    public string? Url => _url;
    public string Title => _title;
    public bool IsAlive => _isAlive;

    public BrowserSession(Guid id, BrowserService owner, string? userDataDir)
    {
        Id = id;
        _owner = owner;
        _userDataDir = userDataDir;
        _url = "about:blank";
        _title = "新建会话";
        _requestsDebounce = new Timer(_ => _owner.RaiseRequestsChanged(Id), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync(string url, string? channel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _opLock.WaitAsync();
        try
        {
            _playwright = await Playwright.CreateAsync();
            if (!string.IsNullOrWhiteSpace(_userDataDir))
            {
                // 持久 profile：cookie/会话存盘，登录一次后续复用（不碰日常浏览器）。LaunchPersistentContextAsync 返回 context（无独立 IBrowser 句柄）。
                var popts = new BrowserTypeLaunchPersistentContextOptions { Headless = false };
                if (!string.IsNullOrWhiteSpace(channel)) popts.Channel = channel;
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, popts);
                _context.Close += OnContextClosed;
                _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
            }
            else
            {
                // 临时 profile（兼容旧行为）：每次新开、不持久。
                var opts = new BrowserTypeLaunchOptions { Headless = false };
                if (!string.IsNullOrWhiteSpace(channel)) opts.Channel = channel;
                _browser = await _playwright.Chromium.LaunchAsync(opts);
                _browser.Disconnected += OnBrowserDisconnected;
                _context = await _browser.NewContextAsync();
                _page = await _context.NewPageAsync();
            }
            _page.Request += OnRequest;
            _page.Response += OnResponse;
            _page.RequestFinished += OnRequestFinished;
            _page.RequestFailed += OnRequestFailed;
            _page.FrameNavigated += OnFrameNavigated;
            if (!string.IsNullOrEmpty(url) && url != "about:blank")
            {
                try { await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }); }
                catch (Exception ex) { Trace.WriteLine($"browser goto {url} failed: {ex.Message}"); }
            }
            UpdateMetadata();
        }
        finally { _opLock.Release(); }
    }

    public async Task NavigateAsync(string url)
    {
        if (!_isAlive || _page is null) return;
        await _opLock.WaitAsync();
        try
        {
            if (!_isAlive || _page is null) return;
            if (url == "about:blank") { await _page.GotoAsync("about:blank"); return; }
            await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            UpdateMetadata();
        }
        catch (Exception ex) { Trace.WriteLine($"browser navigate {url} failed: {ex.Message}"); }
        finally { _opLock.Release(); }
    }

    public async Task<BrowserCookieResult> GetCookiesAsync(List<string>? cookieNames, CancellationToken ct)
    {
        if (!_isAlive || _context is null || _page is null)
            return new BrowserCookieResult { Success = false, Error = "会话已关闭" };
        await _opLock.WaitAsync(ct);
        try
        {
            if (!_isAlive || _context is null || _page is null)
                return new BrowserCookieResult { Success = false, Error = "会话已关闭" };
            var pageUrl = _url;
            var cookies = string.IsNullOrEmpty(pageUrl) || pageUrl == "about:blank"
                ? await _context.CookiesAsync()
                : await _context.CookiesAsync(new[] { pageUrl });
            var list = cookies.ToList();
            if (cookieNames is { Count: > 0 })
            {
                var wanted = cookieNames.ToHashSet();
                list = list.Where(c => wanted.Contains(c.Name)).ToList();
            }
            var cookieInfos = list.Select(c => new CookieInfo
            {
                Name = c.Name,
                Value = Truncate(c.Value),
                Domain = c.Domain ?? string.Empty,
                Path = c.Path ?? string.Empty,
            }).ToList();
            var header = string.Join("; ", list.Where(c => !string.IsNullOrEmpty(c.Value)).Select(c => $"{c.Name}={Truncate(c.Value)}"));
            return new BrowserCookieResult
            {
                Success = true,
                CookieHeader = header,
                Cookies = cookieInfos,
                FinalUrl = pageUrl,
            };
        }
        catch (Exception ex)
        {
            return new BrowserCookieResult { Success = false, Error = ex.Message };
        }
        finally { _opLock.Release(); }
    }

    public IReadOnlyList<NetworkRequestItem> GetRequestsSnapshot()
    {
        lock (_stateLock) return _requests.ToList();
    }

    public void ClearRequests()
    {
        lock (_stateLock) { _byRequest.Clear(); _requests.Clear(); }
    }

    public void MarkDead()
    {
        _isAlive = false;
        _title = "(已关闭)";
    }

    public async ValueTask DisposeAsync()
    {
        _requestsDebounce.Dispose();
        if (_page is not null)
        {
            _page.Request -= OnRequest;
            _page.Response -= OnResponse;
            _page.RequestFinished -= OnRequestFinished;
            _page.RequestFailed -= OnRequestFailed;
            _page.FrameNavigated -= OnFrameNavigated;
        }
        if (_browser is not null) _browser.Disconnected -= OnBrowserDisconnected;
        if (_context is not null) _context.Close -= OnContextClosed;
        await _opLock.WaitAsync();
        try
        {
            try { if (_page is not null) await _page.CloseAsync(); } catch { }
            try { if (_context is not null) await _context.CloseAsync(); } catch { }
            try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
            _playwright?.Dispose();
        }
        catch { }
        finally { _opLock.Release(); }
    }

    // —— Playwright 事件（驱动回调线程；只动请求字典/列表，经 _stateLock 保护；字段更新靠 Avalonia marshal）——

    private void OnRequest(object? sender, IRequest request)
    {
        var item = new NetworkRequestItem(Guid.NewGuid(), Id, DateTimeOffset.UtcNow, request.Method, request.Url);
        try { item.RequestHeaders = JsonSerializer.Serialize(request.Headers); } catch { }
        try { item.ResourceType = request.ResourceType.ToString(); } catch { }
        lock (_stateLock)
        {
            if (!_byRequest.TryAdd(request, item)) return;   // 同请求重复事件去重
            _requests.Add(item);
        }
        ScheduleDebounce();
    }

    private void OnResponse(object? sender, IResponse response)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(response.Request, out item); }
        if (item is null) return;
        try
        {
            item.Status = response.Status;
            item.StatusText = response.StatusText;
            try { item.ResponseHeaders = JsonSerializer.Serialize(response.Headers); } catch { }
            item.IsError = !response.Ok;
        }
        catch { }
        ScheduleDebounce();
    }

    private void OnRequestFinished(object? sender, IRequest request)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(request, out item); }
        if (item is null) return;
        item.IsPending = false;
        item.DurationMs = (int)(DateTimeOffset.UtcNow - item.Time).TotalMilliseconds;
        ScheduleDebounce();
    }

    private void OnRequestFailed(object? sender, IRequest request)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(request, out item); }
        if (item is null) return;
        item.IsPending = false;
        item.IsError = true;
        item.DurationMs = (int)(DateTimeOffset.UtcNow - item.Time).TotalMilliseconds;
        ScheduleDebounce();
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        // 主框架导航 → 更新会话 URL/Title（标签条 + 地址栏同步）。
        if (frame != _page?.MainFrame) return;
        UpdateMetadata();
    }

    private void OnBrowserDisconnected(object? sender, IBrowser browser)
    {
        // 用户手动关了浏览器窗口 / 浏览器崩溃 → 标记会话已死，标签保留供用户关闭（同终端退出范式）。
        if (!_isAlive) return;
        MarkDead();
        _owner.RaiseSessionClosed(Id);
        // 后台清理 Playwright 资源（不阻塞事件线程；已断开，主要释放驱动进程）。
        DisposeAsync().AsTask().SafeFireAndForget();
    }

    private void OnContextClosed(object? sender, IBrowserContext context)
    {
        // 持久模式：context 关闭（用户关浏览器窗口 / 崩溃）→ 标记会话已死。同 OnBrowserDisconnected 语义。
        if (!_isAlive) return;
        MarkDead();
        _owner.RaiseSessionClosed(Id);
        DisposeAsync().AsTask().SafeFireAndForget();
    }

    private void UpdateMetadata()
    {
        try
        {
            if (_page is null) return;
            var u = _page.Url ?? string.Empty;
            _url = u;
            _title = DeriveTitle(u);
            _owner.RaiseSessionsChanged();
        }
        catch { }
    }

    private static string DeriveTitle(string url)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank") return "新建会话";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host;
        return url.Length > 40 ? url[..40] + "…" : url;
    }

    /// <summary>请求通知去抖：每次请求重置 150ms 定时器，合批页面加载的突发请求为一次刷新。</summary>
    private void ScheduleDebounce() =>
        _requestsDebounce.Change(150, Timeout.Infinite);

    private static string Truncate(string? value, int max = 4000) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        (value.Length <= max ? value : value[..max] + "…(已截断)");
}
