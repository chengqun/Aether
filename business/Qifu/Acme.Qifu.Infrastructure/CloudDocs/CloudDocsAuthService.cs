using Acme.AppAuth.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Ports;
using Microsoft.Playwright;

namespace Acme.Qifu.Infrastructure.CloudDocs;

/// <summary>
/// 360Teams 云文档认证服务：用 Playwright 驱动浏览器完成 SSO 登录并捕获 sid cookie，
/// 持久化到 SQLite（仿 YushuAuthService）。对应 Python 版 web_login.py。
/// 半自动：不存储/输入密码，只打开可见浏览器等用户完成登录后读出 sid 并验证。
/// </summary>
public class CloudDocsAuthService : ICloudDocsAuthService
{
    private const string VerifyPath = "/api/v1/files/quota";
    private const string SidCookie = "sid";
    private const string CloudDocsEntryText = "云文档";
    private const string SidConfigKey = "sid";

    // 环境配置（对应 env_profiles.py）
    private static readonly Dictionary<CloudDocsEnv, (string WebBaseUrl, string LoginUrl)> Profiles = new()
    {
        [CloudDocsEnv.Prod] = ("https://doc.360teams.com", "https://doc.360teams.com"),
        [CloudDocsEnv.Sit] = ("https://sit-doc.sk.360shuke.com", "https://sit-360teams.sk.360shuke.com/user/login?redirect=%2Fdoc"),
    };

    private readonly HttpClient _httpClient;
    private readonly IQifuConfigCache _configRepository;
    private readonly ICurrentUser _userContext;
    private readonly ICloudDocsTokenSyncService? _tokenSyncService;
    private readonly ILogger<CloudDocsAuthService> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CloudDocsAuthService(HttpClient httpClient, IQifuConfigCache configCache, ICurrentUser userContext, CloudDocsEnv env, ILogger<CloudDocsAuthService> logger, ICloudDocsTokenSyncService? tokenSyncService = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _configRepository = configCache;
        _userContext = userContext;
        _logger = logger;
        Env = env;
        _tokenSyncService = tokenSyncService;
    }

    /// <inheritdoc />
    public CloudDocsEnv Env { get; }

    /// <inheritdoc />
    public string WebBaseUrl => Profiles[Env].WebBaseUrl;

    private string LoginUrl => Profiles[Env].LoginUrl;

    /// <inheritdoc />
    public async Task<string?> GetSidAsync(CancellationToken ct = default)
    {
        var sid = await ReadLocalSidAsync();

        // 本地有 sid 直接返回（快速路径，不每次都校验，401 时由 ReAuthenticateAsync 处理）
        if (!string.IsNullOrEmpty(sid)) return sid;

        // 本地无 sid：尝试从服务器拉取（手机端无法自行跑 Playwright 登录，依赖桌面端已上传的 sid）
        if (_tokenSyncService != null)
        {
            try
            {
                var synced = await _tokenSyncService.SyncFromServerAsync(ct);
                if (synced)
                {
                    sid = await ReadLocalSidAsync();
                    if (!string.IsNullOrEmpty(sid)) return sid;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从服务器拉取 sid 失败");
            }
        }

        return null;
    }

    /// <summary>从本地 SQLite 读取缓存的 sid（不触发登录/拉取）</summary>
    private async Task<string?> ReadLocalSidAsync()
    {
        var item = await _configRepository.GetAsync(SidConfigKey);
        if (item == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(item.ConfigJson);
            return doc.RootElement.TryGetProperty("sid", out var sidEl) ? sidEl.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        var sid = await GetSidAsync(ct);
        if (string.IsNullOrEmpty(sid)) return false;
        return await VerifySidAsync(sid!, ct);
    }

    /// <inheritdoc />
    public async Task<CloudDocsLoginResult> LoginAsync(int timeoutSeconds = 300, string? channel = null, CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            return await LoginInternalAsync(timeoutSeconds, channel, ct);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CloudDocsLoginResult> ReAuthenticateAsync(int timeoutSeconds = 300, string? channel = null, CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            await _configRepository.DeleteAsync(SidConfigKey);
            return await LoginInternalAsync(timeoutSeconds, channel, ct);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CloudDocsLoginResult> SetSidAsync(string sid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sid))
            return new CloudDocsLoginResult { Success = false, Error = "sid 不能为空" };

        sid = sid.Trim();
        if (!await VerifySidAsync(sid, ct))
            return new CloudDocsLoginResult { Success = false, Error = "sid 验证失败（可能无效或已过期）" };

        await StoreSidAsync(sid, ct);

        // 自动上传 sid 到服务器（供手机端拉取使用）
        SyncSidToServerInBackground();

        return new CloudDocsLoginResult { Success = true, Sid = sid, Message = "sid 已验证并保存" };
    }

    /// <inheritdoc />
    public async Task<bool> VerifySidAsync(string sid, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebBaseUrl.TrimEnd('/') + VerifyPath);
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Cookie", $"{SidCookie}={sid}");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return false;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Playwright 登录主流程（须在 _authLock 内）</summary>
    private async Task<CloudDocsLoginResult> LoginInternalAsync(int timeoutSeconds, string? channel, CancellationToken ct)
    {
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            try
            {
                playwright = await Playwright.CreateAsync();
            }
            catch (Exception ex)
            {
                return new CloudDocsLoginResult
                {
                    Success = false,
                    Error = $"Playwright 不可用：{ex.Message}。请先执行 `playwright install chromium` 安装浏览器。"
                };
            }

            var launchOptions = new BrowserTypeLaunchOptions { Headless = false };
            if (!string.IsNullOrWhiteSpace(channel))
                launchOptions.Channel = channel;

            browser = await playwright.Chromium.LaunchAsync(launchOptions);
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                await page.GotoAsync(LoginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            }
            catch (Exception ex)
            {
                return new CloudDocsLoginResult { Success = false, Error = $"打开登录页失败：{ex.Message}" };
            }

            _logger.LogInformation("已打开 {LoginUrl}，请在浏览器完成 SSO 登录", LoginUrl);

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var lastAutoOpenAttempt = DateTime.MinValue;
            string? sid = null;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var cookies = await context.CookiesAsync();
                foreach (var candidate in CollectSids(cookies))
                {
                    if (await VerifySidAsync(candidate, ct))
                    {
                        sid = candidate;
                        break;
                    }
                }
                if (sid != null) break;

                if (DateTime.UtcNow - lastAutoOpenAttempt >= TimeSpan.FromSeconds(5))
                {
                    lastAutoOpenAttempt = DateTime.UtcNow;
                    if (await ClickCloudDocsEntryAsync(page))
                        _logger.LogInformation("已点击门户「云文档」入口，等待会话生效");
                }

                await page.WaitForTimeoutAsync(2000);
            }

            if (string.IsNullOrEmpty(sid))
            {
                return new CloudDocsLoginResult
                {
                    Success = false,
                    Error = $"登录超时（{timeoutSeconds}s）未捕获到有效 sid，请重试或加大超时。"
                };
            }

            await StoreSidAsync(sid!, ct);
            _logger.LogInformation("登录成功，sid 已保存");

            // 自动上传 sid 到服务器（供手机端拉取使用）
            SyncSidToServerInBackground();

            return new CloudDocsLoginResult { Success = true, Sid = sid, Message = "登录成功，sid 已保存" };
        }
        catch (PlaywrightException ex)
        {
            return new CloudDocsLoginResult
            {
                Success = false,
                Error = $"Playwright 启动/控制浏览器失败：{ex.Message}。若未安装 Chromium，请执行 `playwright install chromium` 或用 channel=chrome 复用本地 Chrome。"
            };
        }
        finally
        {
            try { if (browser != null) await browser.CloseAsync(); } catch { }
            try { playwright?.Dispose(); } catch { }
        }
    }

    /// <summary>从 cookies 中收集去重的 sid 候选（SSO 可能在父域设置 sid，故不按 host 过滤）</summary>
    private static List<string> CollectSids(IEnumerable<BrowserContextCookiesResult> cookies)
    {
        var seen = new List<string>();
        foreach (var cookie in cookies)
        {
            if (cookie.Name != SidCookie) continue;
            var value = cookie.Value;
            if (!string.IsNullOrEmpty(value) && !seen.Contains(value))
                seen.Add(value);
        }
        return seen;
    }

    /// <summary>登录后门户可能停在 360Teams 门户页，需点击「云文档」入口才会下发 sid（对应 click_cloud_docs_entry）</summary>
    private static async Task<bool> ClickCloudDocsEntryAsync(IPage page)
    {
        ILocator?[] candidates =
        {
            TryGetFirst(page.GetByRole(AriaRole.Link, new() { Name = CloudDocsEntryText, Exact = true })),
            TryGetFirst(page.GetByRole(AriaRole.Button, new() { Name = CloudDocsEntryText, Exact = true })),
            TryGetFirst(page.GetByText(CloudDocsEntryText, new() { Exact = true })),
        };

        foreach (var locator in candidates)
        {
            if (locator == null) continue;
            try
            {
                if (await locator.CountAsync() < 1) continue;
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 1000 });
                return true;
            }
            catch
            {
                continue;
            }
        }
        return false;
    }

    private static ILocator? TryGetFirst(ILocator locator)
    {
        try { return locator.First; }
        catch { return null; }
    }

    /// <summary>持久化 sid 到 SQLite（带 env 与捕获时间）</summary>
    private async Task StoreSidAsync(string sid, CancellationToken ct)
    {
        var data = new Dictionary<string, object>
        {
            ["sid"] = sid,
            ["env"] = Env.ToString().ToLowerInvariant(),
            ["captured_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        await _configRepository.UpsertAsync(SidConfigKey, JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>后台异步上传 sid 到服务器（fire-and-forget，不阻塞登录流程）</summary>
    private void SyncSidToServerInBackground()
    {
        if (_tokenSyncService == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var success = await _tokenSyncService.SyncToServerAsync();
                if (success)
                    _logger.LogInformation("sid 已同步到服务器");
                else
                    _logger.LogWarning("sid 同步到服务器失败");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "sid 同步到服务器异常");
            }
        });
    }
}
