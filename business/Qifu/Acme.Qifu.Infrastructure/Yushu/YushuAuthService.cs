using Acme.Platform.Domain;
using Acme.Qifu.Domain.Yushu;
using Acme.Qifu.Domain.Ports;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Qifu.Infrastructure.Yushu;

/// <summary>
/// 毓数平台 OAuth2 认证服务，使用 SQLite 缓存 token 和客户端配置。
/// 支持跨端同步：桌面端认证后自动上传 token 到服务器，手机端从服务器拉取。
/// </summary>
public class YushuAuthService : IYushuAuthService
{
    private const string OAuthClientConfigUrl = "http://lowcode-app.daikuan.qihoo.net/openapi/v1/oauth2/client?client=agent";
    private const string OAuthAuthorizeUrl = "https://lowcode.daikuan.qihoo.net/?#/login/oauth";
    private const string OAuthTokenUrl = "http://lowcode-app.daikuan.qihoo.net/oauth2/token";
    private const string OAuthScope = "read";
    private const int CallbackPort = 8888;
    private const string CallbackPath = "/callback";
    private const int AuthTimeoutSeconds = 120;

    // SQLite 配置键常量
    private const string TokenConfigKey = "token";
    private const string ClientConfigKey = "client_config";

    private readonly HttpClient _httpClient;
    private readonly IQifuConfigCache _configCache;
    private readonly ICurrentUser _userContext;
    private readonly IYushuTokenSyncService? _tokenSyncService;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private string? _cachedToken;
    private DateTime? _cachedTokenExpiry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public YushuAuthService(HttpClient httpClient, IQifuConfigCache configCache, ICurrentUser userContext, IYushuTokenSyncService? tokenSyncService = null)
    {
        _httpClient = httpClient;
        _configCache = configCache;
        _userContext = userContext;
        _tokenSyncService = tokenSyncService;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            // 1. 先检查内存缓存
            if (_cachedToken != null && _cachedTokenExpiry > DateTime.UtcNow)
            {
                return _cachedToken;
            }

            // 2. 读取 SQLite 缓存
            var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");
            var tokenItem = await _configCache.GetAsync(userId, TokenConfigKey);

            if (tokenItem != null)
            {
                var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenItem.ConfigJson, JsonOptions);
                var accessToken = tokenData.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                var expiresAt = tokenData.TryGetProperty("expires_at", out var ea) ? ea.GetInt64() : 0;

                if (!string.IsNullOrEmpty(accessToken) && expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
                {
                    _cachedToken = accessToken;
                    _cachedTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime;
                    return accessToken;
                }
            }

            // 3. 尝试从服务器拉取 token（手机端无法自行认证，依赖桌面端已上传的 token）
            if (_tokenSyncService != null)
            {
                try
                {
                    var synced = await _tokenSyncService.SyncFromServerAsync(ct);
                    if (synced)
                    {
                        // 重新从 SQLite 读取
                        var syncedItem = await _configCache.GetAsync(userId, TokenConfigKey);
                        if (syncedItem != null)
                        {
                            var syncedData = JsonSerializer.Deserialize<JsonElement>(syncedItem.ConfigJson, JsonOptions);
                            var syncedAccessToken = syncedData.TryGetProperty("access_token", out var sat) ? sat.GetString() : null;
                            var syncedExpiresAt = syncedData.TryGetProperty("expires_at", out var sea) ? sea.GetInt64() : 0;

                            if (!string.IsNullOrEmpty(syncedAccessToken) && syncedExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
                            {
                                _cachedToken = syncedAccessToken;
                                _cachedTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(syncedExpiresAt).UtcDateTime;
                                return syncedAccessToken;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[毓数] 从服务器拉取 token 失败: {ex.Message}");
                }
            }

            // 4. 服务器也无有效 token，触发浏览器登录（桌面端）
            var newToken = await AuthenticateAsync(ct);
            _cachedToken = newToken;
            // 重新从缓存读取实际的过期时间
            var refreshedItem = await _configCache.GetAsync(userId, TokenConfigKey);
            if (refreshedItem != null)
            {
                var refreshedData = JsonSerializer.Deserialize<JsonElement>(refreshedItem.ConfigJson, JsonOptions);
                var refreshedExpires = refreshedData.TryGetProperty("expires_at", out var rea) ? rea.GetInt64() : 0;
                _cachedTokenExpiry = refreshedExpires > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(refreshedExpires).UtcDateTime
                    : DateTime.UtcNow.AddHours(24);
            }
            else
            {
                _cachedTokenExpiry = DateTime.UtcNow.AddHours(24);
            }

            return newToken;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        // 1. 先检查内存缓存
        if (_cachedToken != null && _cachedTokenExpiry > DateTime.UtcNow)
        {
            return true;
        }

        // 2. 读取 SQLite 缓存
        var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");
        var tokenItem = await _configCache.GetAsync(userId, TokenConfigKey);

        if (tokenItem != null)
        {
            var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenItem.ConfigJson, JsonOptions);
            var expiresAt = tokenData.TryGetProperty("expires_at", out var ea) ? ea.GetInt64() : 0;

            // token 有效（留 60 秒缓冲）
            if (expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await GetAccessTokenAsync(ct);
            return token;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[毓数] LoginAsync 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 强制重新认证（当 API 返回 401 时调用）
    /// </summary>
    public async Task<string> ReAuthenticateAsync(CancellationToken ct = default)
    {
        _cachedToken = null;
        _cachedTokenExpiry = null;

        // 清除缓存的 token
        var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");
        await _configCache.DeleteAsync(userId, TokenConfigKey);

        return await GetAccessTokenAsync(ct);
    }

    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");

        // 1. 获取 OAuth client 配置
        var clientConfig = await GetClientConfigAsync(userId, ct);
        var clientId = clientConfig["clientId"]!.ToString()!;
        var clientSecret = clientConfig["clientSecret"]!.ToString()!;

        // 2. 生成 state 和 nonce
        var state = GenerateRandomString(7);
        var nonce = GenerateRandomString(7);

        // 3. 查找可用端口
        var port = FindAvailablePort(CallbackPort);
        var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";

        // 4. 构建授权 URL 并打开浏览器
        var authUrl = $"{OAuthAuthorizeUrl}?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&state={state}&nonce={nonce}&scope={OAuthScope}";

        Console.WriteLine($"[毓数] 正在打开浏览器进行登录... 回调端口: {port}");

        OpenBrowser(authUrl);

        // 5. 启动本地 HTTP 监听，等待回调
        var authCode = await WaitForCallbackAsync(port, AuthTimeoutSeconds, ct);

        if (string.IsNullOrEmpty(authCode))
        {
            throw new InvalidOperationException("毓数平台登录超时，请重试");
        }

        // 6. 使用 authorization code 换取 token
        var serverRedirectUri = $"http://lowcode-app.daikuan.qihoo.net/login/oauth2/code/{clientId}";
        var tokenResponse = await ExchangeCodeForTokenAsync(authCode, serverRedirectUri, clientId, clientSecret, ct);

        // 7. 计算过期时间并保存到 SQLite
        var expiresInValue = tokenResponse.TryGetValue("expires_in", out var ei) ? ei : default;
        var expiresIn = expiresInValue.ValueKind == JsonValueKind.Number ? ei.GetInt64() : 604800;
        var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;

        var tokenDataToSave = new Dictionary<string, object>
        {
            ["access_token"] = tokenResponse["access_token"]!.GetString()!,
            ["refresh_token"] = tokenResponse.TryGetValue("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
            ["expires_at"] = expiresAt,
            ["token_type"] = tokenResponse.TryGetValue("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer",
        };

        await _configCache.UpsertAsync(new QifuConfigCacheItem
        {
            UserId = userId,
            ConfigKey = TokenConfigKey,
            ConfigJson = JsonSerializer.Serialize(tokenDataToSave, JsonOptions),
        });

        Console.WriteLine("[毓数] 登录成功！");

        // 8. 自动上传 token 到服务器（供手机端拉取使用）
        if (_tokenSyncService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var success = await _tokenSyncService.SyncToServerAsync();
                    if (success)
                    {
                        Console.WriteLine("[毓数] Token 已同步到服务器");
                    }
                    else
                    {
                        Console.WriteLine("[毓数] Token 同步到服务器失败");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[毓数] Token 同步到服务器异常: {ex.Message}");
                }
            });
        }

        return tokenDataToSave["access_token"].ToString()!;
    }

    private async Task<Dictionary<string, JsonElement>> GetClientConfigAsync(string userId, CancellationToken ct)
    {
        // 先尝试从 SQLite 缓存读取
        var cachedItem = await _configCache.GetAsync(userId, ClientConfigKey);
        if (cachedItem != null)
        {
            var data = JsonSerializer.Deserialize<JsonElement>(cachedItem.ConfigJson, JsonOptions);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in data.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }
            return dict;
        }

        // 从服务端获取
        var response = await _httpClient.GetAsync(OAuthClientConfigUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        if (!result.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var msg = result.TryGetProperty("errorMsg", out var err) ? err.GetString() : "未知错误";
            throw new InvalidOperationException($"获取 OAuth client 配置失败: {msg}");
        }

        var dataEl = result.GetProperty("data");
        var configDict = new Dictionary<string, JsonElement>
        {
            ["clientId"] = dataEl.GetProperty("clientId"),
            ["clientSecret"] = dataEl.GetProperty("clientSecret"),
        };

        // 保存到 SQLite
        await _configCache.UpsertAsync(new QifuConfigCacheItem
        {
            UserId = userId,
            ConfigKey = ClientConfigKey,
            ConfigJson = JsonSerializer.Serialize(configDict, JsonOptions),
        });

        return configDict;
    }

    private async Task<Dictionary<string, JsonElement>> ExchangeCodeForTokenAsync(
        string code, string redirectUri, string clientId, string clientSecret, CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["clientId"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };

        request.Content = new FormUrlEncodedContent(formData);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in result.EnumerateObject())
        {
            dict[prop.Name] = prop.Value;
        }

        return dict;
    }

    private async Task<string?> WaitForCallbackAsync(int port, int timeoutSeconds, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}{CallbackPath}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            Console.WriteLine($"[毓数] 无法在端口 {port} 启动回调监听");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var context = await listener.GetContextAsync();

            var code = context.Request.QueryString["code"];

            // 返回响应页面
            var html = !string.IsNullOrEmpty(code)
                ? BuildSuccessHtml()
                : BuildErrorHtml("授权失败", "未收到授权码");

            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, cts.Token);
            context.Response.Close();

            return code;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[毓数] 登录超时");
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    #region 辅助方法

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
        catch (Exception ex)
        {
            Console.WriteLine($"[毓数] 无法自动打开浏览器，请手动访问: {url} ({ex.Message})");
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
        @"<body><div class=""container""><div class=""icon"">✓</div><h1>登录成功</h1>" +
        @"<p>授权完成，请返回终端查看状态</p>" +
        @"<div class=""tip"">请返回终端查看登录状态<br>当前页面可以关闭</div></div></body></html>";

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
        @"<div class=""tip"">请返回终端重新执行登录命令</div></div></body></html>";

    #endregion
}
