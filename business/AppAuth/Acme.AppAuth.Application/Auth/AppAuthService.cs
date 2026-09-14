using System.Net.Http;
using System.Text.Json;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Application.Auth;

/// <summary>
/// App-server authentication via OAuth2 password grant (ABP <c>connect/token</c>).
/// Acquired tokens are persisted in <see cref="IAppTokenStore"/>; subsequent
/// app-server HTTP calls are authenticated automatically by the bearer handler.
/// <para>
/// Login/refresh use a plain <see cref="HttpClient"/> (no bearer handler) because
/// <c>connect/token</c> is itself the token-acquiring endpoint — routing it through
/// the authenticated client would create a DI cycle (handler → refresher → client → handler).
/// </para>
/// </summary>
public sealed class AppAuthService : IAppAuthService, IAppTokenRefresher
{
    private readonly HttpClient _http;
    private readonly IAppTokenStore _store;
    private readonly AppServerOptions _options;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // 刷新令牌轮换的 single-flight 守卫（见 SingleFlightRefreshAsync 注释）。
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task<bool>? _inFlightRefresh;

    public AppAuthService(HttpClient http, IAppTokenStore store, AppServerOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    /// <remarks>只更新 <see cref="AppServerOptions.BaseUrl"/>，不再设 <c>HttpClient.BaseAddress</c>——
    /// 单例 HttpClient 在首次请求后不允许改 <c>BaseAddress</c>（手机端启动期静默
    /// <see cref="RefreshAsync"/> 会先用此 client，登录页再调 ConfigureBaseUrl 即抛
    /// InvalidOperationException）。<see cref="PostFormAsync"/> 直接用 _options.BaseUrl
    /// 拼绝对 URI，故运行时改地址即时生效，且不依赖可变 BaseAddress。</remarks>
    public void ConfigureBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _options.BaseUrl = url;
    }

    /// <inheritdoc/>
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new LoginResult { Error = "请输入用户名和密码" };

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        if (!string.IsNullOrEmpty(_options.Scope)) form["scope"] = _options.Scope;
        if (!string.IsNullOrEmpty(_options.Tenant)) form["__tenant"] = _options.Tenant;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return new LoginResult { Error = "未配置服务器地址，请在登录页输入服务器地址" };

        HttpResponseMessage response;
        try
        {
            response = await PostFormAsync("connect/token", form, ct);
        }
        catch (Exception ex)
        {
            return new LoginResult { Error = $"登录请求失败（无响应，检查网络/证书/服务端）：{ex.Message}" };
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return new LoginResult { Error = ParseLoginError(body, (int)response.StatusCode) };
        }

        var token = await ParseTokenAsync(response, username, ct);
        await _store.SaveAsync(token, ct);
        return new LoginResult { Success = true, AccessToken = token.AccessToken };
    }

    /// <inheritdoc/>
    public Task<bool> RefreshAsync(CancellationToken ct = default) => SingleFlightRefreshAsync(ct);

    /// <summary>single-flight 刷新：并发 401（启动时证券同步+聊天+动态并行全 401）若各自发刷新 →
    /// N 个并发 refresh_token grant，OpenIddict 轮换后首个成功、余者失败 → 败者 ClearAsync 抹掉
    /// 胜者刚存的 token → 会话有效却被登出。leader 独占刷新，followers 等同一个 in-flight Task。</summary>
    private async Task<bool> SingleFlightRefreshAsync(CancellationToken ct)
    {
        Task<bool> shared;
        var leader = false;
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inFlightRefresh is null)
            {
                // 核心刷新与任一调用者的 CT 解耦——发起者取消不应让并行等待者陪葬。
                _inFlightRefresh = RefreshCoreAsync();
                leader = true;
            }
            shared = _inFlightRefresh;
        }
        finally
        {
            _refreshLock.Release();
        }

        try
        {
            return await shared.ConfigureAwait(false);
        }
        finally
        {
            if (leader)
            {
                // 维护清位不受调用者取消干扰（WaitAsync(None)），否则缓存的任务永久残留。
                await _refreshLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_inFlightRefresh, shared))
                        _inFlightRefresh = null;
                }
                finally
                {
                    _refreshLock.Release();
                }
            }
        }
    }

    /// <summary>单次刷新核心（被 single-flight 独占调用，无 CT——见 <see cref="SingleFlightRefreshAsync"/>）。</summary>
    private async Task<bool> RefreshCoreAsync()
    {
        var refresh = await _store.GetRefreshTokenAsync();
        if (string.IsNullOrEmpty(refresh)) return false;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        if (!string.IsNullOrEmpty(_options.Tenant)) form["__tenant"] = _options.Tenant;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            await _store.ClearAsync();
            return false;
        }

        HttpResponseMessage response;
        try
        {
            response = await PostFormAsync("connect/token", form, CancellationToken.None);
        }
        catch
        {
            await _store.ClearAsync();
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            await _store.ClearAsync();
            return false;
        }

        var username = await _store.GetUsernameAsync() ?? "";
        var token = await ParseTokenAsync(response, username, CancellationToken.None);
        await _store.SaveAsync(token);
        return true;
    }

    /// <inheritdoc/>
    public async Task LogoutAsync(CancellationToken ct = default) => await _store.ClearAsync(ct);

    /// <inheritdoc/>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        var access = await _store.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(access)) return false;
        var expires = await _store.GetExpiresAtAsync(ct);
        return expires is null || expires > DateTimeOffset.UtcNow;
    }

    private static async Task<AppToken> ParseTokenAsync(HttpResponseMessage response, string username, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonSerializer.Deserialize<JsonElement>(json, Json);
        var access = doc.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
        var refresh = doc.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        var expiresIn = doc.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        return new AppToken
        {
            AccessToken = access,
            RefreshToken = refresh,
            Username = username,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        };
    }

    /// <summary>解析 OpenIddict/ABP 登录错误体，给出可读的中文提示（对齐 AlphaAgent ParseLoginError）。</summary>
    private static string ParseLoginError(string? errBody, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(errBody))
            return $"登录失败：服务端返回 {statusCode}";

        // ABP/OpenIddict 错误体：{"error":"invalid_grant","error_description":"..."}
        string? desc = null;
        try
        {
            using var doc = JsonDocument.Parse(errBody);
            if (doc.RootElement.TryGetProperty("error_description", out var d))
                desc = d.GetString();
            else if (doc.RootElement.TryGetProperty("error", out var e))
                desc = e.GetString();
        }
        catch { }

        var lower = (desc ?? errBody).ToLowerInvariant();
        if (lower.Contains("locked") || lower.Contains("锁定"))
            return "账号已被锁定（多次密码错误），请等 15 分钟自动解锁，或联系管理员在后台解锁";
        if (lower.Contains("invalid") && (lower.Contains("password") || lower.Contains("credential") || lower.Contains("username")))
            return "用户名或密码错误";
        if (lower.Contains("not_found") || lower.Contains("不存在"))
            return "用户不存在";
        return $"登录失败：{desc ?? errBody}";
    }

    /// <summary>POSTs a form to a relative endpoint on the plain (no-auth) client.
    /// Adds the <c>__tenant</c> HEADER (mirrors AlphaAgent's TenantHeaderDelegatingHandler on its
    /// authenticated login client) so ABP resolves the tenant on the token endpoint and issues a
    /// tenant-scoped JWT — form-field alone is unreliable for refresh_token grant.
    /// Throws on transport failure — callers handle (login surfaces the message, refresh returns false).</summary>
    private async Task<HttpResponseMessage> PostFormAsync(string endpoint, IDictionary<string, string> form, CancellationToken ct)
    {
        // 用 _options.BaseUrl 拼绝对 URI（不依赖 HttpClient.BaseAddress——单例 client 首次请求后不可变）。
        var requestUri = BuildRequestUri(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (!string.IsNullOrWhiteSpace(_options.Tenant))
            request.Headers.Add("__tenant", _options.Tenant);
        return await _http.SendAsync(request, ct);
    }

    /// <summary>把相对端点拼成绝对 URI（基于 _options.BaseUrl）；已是绝对 URI 则原样返回。</summary>
    private string BuildRequestUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        var baseUri = (_options.BaseUrl ?? string.Empty).TrimEnd('/');
        return string.IsNullOrEmpty(baseUri) ? endpoint : $"{baseUri}/{endpoint.TrimStart('/')}";
    }
}
