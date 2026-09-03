using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Qifu.Domain.Yushu;
using Microsoft.Extensions.Logging;

namespace Acme.Qifu.Infrastructure.Yushu;

/// <summary>
/// mcphub 网关 OAuth 鉴权服务：浏览器登录 → loopback /callback 回调 → 落盘 dashboard JWT
/// 到 <c>~/.local/share/mcphub/credentials.json</c>（与 Python sql-query skill / mcphub 插件共享同一文件、
/// 同一 profile 名 <c>mcphub-daikuan-qihoo-net</c>）。token 走 <c>x-auth-token</c> 头，有效期约 7 天；
/// 失效时 <see cref="GetAccessTokenAsync"/> 自动重登。对照 <c>sql_query.py</c> 的
/// <c>_browser_login</c>/<c>_ensure_jwt</c>/<c>_current_user</c>。
/// </summary>
public class McphubAuthService : IMcphubAuthService
{
    private const string McphubBaseUrl = "https://mcphub.daikuan.qihoo.net";
    private const string AuthorizeEndpoint = McphubBaseUrl + "/api/auth/cli/authorize";
    private const string UserEndpoint = McphubBaseUrl + "/api/auth/user";

    /// <summary>profile 名（与 skill <c>_make_profile_name</c> 对齐：netloc 的点→横线）。</summary>
    private const string ProfileName = "mcphub-daikuan-qihoo-net";

    private const string CallbackPath = "/callback";
    private const int CallbackStartPort = 8890;
    private const int AuthTimeoutSeconds = 120;
    private const int ValidationCacheSeconds = 300; // 5 min — 避免每次 tools/call 都打 /api/auth/user

    private readonly HttpClient _httpClient;
    private readonly ILogger<McphubAuthService> _logger;
    private readonly IMcphubTokenSyncService? _tokenSyncService;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private string? _cachedToken;
    private DateTime _cachedValidatedAt = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public McphubAuthService(HttpClient httpClient, ILogger<McphubAuthService> logger, IMcphubTokenSyncService? tokenSyncService = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _tokenSyncService = tokenSyncService;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>登录成功拿到新 JWT 后 fire-and-forget 上传服务器（供手机端拉取）。失败不阻断登录主流程。</summary>
    private void UploadToServer(string token, string? username)
    {
        if (_tokenSyncService is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _tokenSyncService.SyncToServerAsync(token, username);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mcphub JWT 上传服务器失败（不影响本地使用）");
            }
        });
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            // 1. 内存缓存（近期校验过）直接返回
            if (_cachedToken != null && (DateTime.UtcNow - _cachedValidatedAt).TotalSeconds < ValidationCacheSeconds)
            {
                return _cachedToken;
            }

            // 2. 读凭据文件，校验 /api/auth/user
            var token = TryReadCredentialsToken();
            if (!string.IsNullOrEmpty(token) && await ValidateTokenAsync(token, ct))
            {
                _cachedToken = token;
                _cachedValidatedAt = DateTime.UtcNow;
                return token;
            }

            // 3. 无有效 token → 浏览器登录
            var fresh = await BrowserLoginAsync(ct);
            SaveCredentials(fresh.Token, fresh.Username);
            UploadToServer(fresh.Token, fresh.Username);
            _cachedToken = fresh.Token;
            _cachedValidatedAt = DateTime.UtcNow;
            return fresh.Token;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        var token = TryReadCredentialsToken();
        if (string.IsNullOrEmpty(token)) return false;
        return await ValidateTokenAsync(token, ct);
    }

    /// <inheritdoc />
    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        try
        {
            await _authLock.WaitAsync(ct);
            try
            {
                var fresh = await BrowserLoginAsync(ct);
                SaveCredentials(fresh.Token, fresh.Username);
                UploadToServer(fresh.Token, fresh.Username);
                _cachedToken = fresh.Token;
                _cachedValidatedAt = DateTime.UtcNow;
                return fresh.Token;
            }
            finally
            {
                _authLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcphub 登录失败");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> ReAuthenticateAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            _cachedToken = null;
            _cachedValidatedAt = DateTime.MinValue;
            var fresh = await BrowserLoginAsync(ct);
            SaveCredentials(fresh.Token, fresh.Username);
            UploadToServer(fresh.Token, fresh.Username);
            _cachedToken = fresh.Token;
            _cachedValidatedAt = DateTime.UtcNow;
            return fresh.Token;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>GET /api/auth/user 校验 token；200 且含 user.username 视为有效。</summary>
    private async Task<bool> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
            req.Headers.Add("x-auth-token", token);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("user", out var user)
                && user.TryGetProperty("username", out var name)
                && !string.IsNullOrEmpty(name.GetString());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>浏览器 OAuth：POST /api/auth/cli/authorize 拿授权 URL → 开浏览器 → loopback 收 token。</summary>
    private async Task<(string Token, string Username)> BrowserLoginAsync(CancellationToken ct)
    {
        var state = GenerateRandomString(16);
        var port = FindAvailablePort(CallbackStartPort);
        var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";

        // 1. 请求授权 URL
        var body = JsonSerializer.Serialize(new { redirectUri, state }, JsonOptions);
        using var authReq = new HttpRequestMessage(HttpMethod.Post, AuthorizeEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var authResp = await _httpClient.SendAsync(authReq, ct);
        var authJson = await authResp.Content.ReadAsStringAsync(ct);
        if (!authResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"请求 mcphub 授权地址失败(HTTP {(int)authResp.StatusCode}): {authJson}");
        }
        string? authUrl;
        try
        {
            using var doc = JsonDocument.Parse(authJson);
            authUrl = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        }
        catch (JsonException)
        {
            authUrl = null;
        }
        if (string.IsNullOrEmpty(authUrl))
        {
            throw new InvalidOperationException("mcphub 服务端未返回授权 URL。");
        }

        _logger.LogInformation("正在打开浏览器进行 mcphub 登录，回调端口: {Port}", port);
        OpenBrowser(authUrl);

        // 2. loopback 等回调（token+state+username）
        var result = await WaitForCallbackAsync(port, AuthTimeoutSeconds, state, ct);
        return (result.Token, result.Username);
    }

    private async Task<(string Token, string Username)> WaitForCallbackAsync(
        int port, int timeoutSeconds, string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}{CallbackPath}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"无法在端口 {port} 启动回调监听: {ex.Message}", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var context = await listener.GetContextAsync();
            var token = context.Request.QueryString["token"];
            var state = context.Request.QueryString["state"];
            var username = context.Request.QueryString["username"];

            var ok = !string.IsNullOrEmpty(token)
                && state == expectedState
                && !string.IsNullOrEmpty(username);
            var html = ok ? BuildSuccessHtml() : BuildErrorHtml("授权失败", "未收到有效 token / state 不匹配");

            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, cts.Token);
            context.Response.Close();

            if (!ok)
            {
                throw new InvalidOperationException("mcphub 登录回调参数无效（state 不匹配或缺少 token）");
            }
            return (token!, username!);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("mcphub 登录超时，请重试");
        }
        finally
        {
            listener.Stop();
        }
    }

    #region 凭据文件读写

    private static string CredentialsPath()
    {
        var home = GetHomePath();
        var legacy = Path.Combine(home, ".mcphub", "credentials.json");
        return File.Exists(legacy)
            ? legacy
            : Path.Combine(home, ".local", "share", "mcphub", "credentials.json");
    }

    private static string? TryReadCredentialsToken()
    {
        var path = CredentialsPath();
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            // current → profiles[current].token
            if (root.TryGetProperty("current", out var cur))
            {
                var current = cur.GetString();
                if (!string.IsNullOrEmpty(current)
                    && root.TryGetProperty("profiles", out var profiles)
                    && profiles.TryGetProperty(current, out var profile)
                    && profile.TryGetProperty("token", out var tok))
                {
                    var t = tok.GetString();
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            // 回退：canonical profile 名
            if (root.TryGetProperty("profiles", out var profiles2)
                && profiles2.TryGetProperty(ProfileName, out var profile2)
                && profile2.TryGetProperty("token", out var tok2))
            {
                var t = tok2.GetString();
                if (!string.IsNullOrEmpty(t)) return t;
            }
            // 最后回退：首个 profile 的 token
            if (root.TryGetProperty("profiles", out var profiles3) && profiles3.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in profiles3.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("token", out var tok3))
                    {
                        var t = tok3.GetString();
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                }
            }
        }
        catch
        {
            /* skip unreadable credential file */
        }
        return null;
    }

    private static void SaveCredentials(string token, string username)
    {
        var path = CredentialsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Dictionary<string, object> doc;
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path), JsonOptions);
                doc = existing.ValueKind == JsonValueKind.Object
                    ? existing.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone() as object)
                    : new Dictionary<string, object>();
            }
            else
            {
                doc = new Dictionary<string, object>();
            }
        }
        catch
        {
            doc = new Dictionary<string, object>();
        }

        // 原地刷新 profile：保留既有 url（skill/插件可能已写 http url，仅作选取标识）。
        var entry = new Dictionary<string, object>
        {
            ["url"] = McphubBaseUrl + "/",
            ["tokenKind"] = "jwt",
            ["token"] = token,
            ["username"] = username,
            ["savedAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (!doc.TryGetValue("profiles", out var profilesObj) || profilesObj is not JsonElement profilesEl || profilesEl.ValueKind != JsonValueKind.Object)
        {
            doc["profiles"] = new Dictionary<string, object> { [ProfileName] = entry };
        }
        else
        {
            var profiles = profilesEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone() as object);
            // 若已存在同名 profile，保留其 url（避免覆盖插件写的 http url 致互不命中）。
            if (profiles.TryGetValue(ProfileName, out var existing) && existing is JsonElement el && el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("url", out var url))
            {
                entry["url"] = url.GetString() ?? McphubBaseUrl + "/";
            }
            profiles[ProfileName] = entry;
            doc["profiles"] = profiles;
        }
        doc["current"] = ProfileName;

        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
    }

    #endregion

    #region 辅助

    private static string GetHomePath() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string GenerateRandomString(int length)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static int FindAvailablePort(int startPort, int maxAttempts = 100)
    {
        for (var port = startPort; port < startPort + maxAttempts; port++)
        {
            try
            {
                using var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}{CallbackPath}/");
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                continue;
            }
        }
        throw new InvalidOperationException($"无法找到可用端口 (尝试范围: {startPort}-{startPort + maxAttempts})");
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
            }
            else
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
        }
        catch
        {
            /* 调用方日志已记录端口；浏览器开失败时用户可手动复制 URL */
        }
    }

    private static string BuildSuccessHtml() =>
        @"<!DOCTYPE html><html lang=""zh-CN""><head><meta charset=""UTF-8"">" +
        @"<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;" +
        @"background:linear-gradient(135deg,#667eea 0%,#764ba2 100%);margin:0;padding:0;" +
        @"min-height:100vh;display:flex;align-items:center;justify-content:center}" +
        @".container{background:#fff;padding:40px;border-radius:12px;box-shadow:0 10px 25px rgba(0,0,0,.1);" +
        @"text-align:center;max-width:400px;margin:20px}.icon{font-size:48px;color:#4CAF50;margin-bottom:20px}" +
        @"h1{color:#333;margin:0 0 10px;font-size:24px}p{color:#666;margin:10px 0;line-height:1.6}" +
        @".tip{margin-top:20px;padding:15px;background:#f8f9fa;border-radius:8px;color:#666;font-size:14px}</style></head>" +
        @"<body><div class=""container""><div class=""icon"">✓</div><h1>mcphub 登录成功</h1>" +
        @"<p>授权完成，请返回 Aether 查看连接器状态</p>" +
        @"<div class=""tip"">请返回 Aether 查看登录状态<br>当前页面可以关闭</div></div></body></html>";

    private static string BuildErrorHtml(string title, string message) =>
        @$"<!DOCTYPE html><html lang=""zh-CN""><head><meta charset=""UTF-8"">" +
        @"<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;" +
        @"background:linear-gradient(135deg,#667eea 0%,#764ba2 100%);margin:0;padding:0;" +
        @"min-height:100vh;display:flex;align-items:center;justify-content:center}" +
        @".container{background:#fff;padding:40px;border-radius:12px;box-shadow:0 10px 25px rgba(0,0,0,.1);" +
        @"text-align:center;max-width:400px;margin:20px}.icon{font-size:48px;color:#f44336;margin-bottom:20px}" +
        @"h1{color:#333;margin:0 0 10px;font-size:24px}p{color:#666;margin:10px 0;line-height:1.6}" +
        @".tip{margin-top:20px;padding:15px;background:#f8f9fa;border-radius:8px;color:#666;font-size:14px}</style></head>" +
        @$"<body><div class=""container""><div class=""icon"">✗</div><h1>{title}</h1>" +
        @$"<p>{message}</p>" +
        @"<div class=""tip"">请返回 Aether 重新登录</div></div></body></html>";

    #endregion
}
