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

    public AppAuthService(HttpClient http, IAppTokenStore store, AppServerOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new LoginResult { Error = "username and password are required" };

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

        if (_http.BaseAddress is null)
            return new LoginResult { Error = "No APP_BASEURL configured (set the APP_BASEURL env var)." };

        HttpResponseMessage response;
        try
        {
            response = await PostFormAsync("connect/token", form, ct);
        }
        catch (Exception ex)
        {
            return new LoginResult { Error = $"network error: {ex.Message}" };
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return new LoginResult { Error = $"login failed: {(int)response.StatusCode} {body}" };
        }

        var token = await ParseTokenAsync(response, username, ct);
        await _store.SaveAsync(token, ct);
        return new LoginResult { Success = true, AccessToken = token.AccessToken };
    }

    /// <inheritdoc/>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var refresh = await _store.GetRefreshTokenAsync(ct);
        if (string.IsNullOrEmpty(refresh)) return false;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        if (!string.IsNullOrEmpty(_options.Tenant)) form["__tenant"] = _options.Tenant;

        if (_http.BaseAddress is null)
        {
            await _store.ClearAsync(ct);
            return false;
        }

        HttpResponseMessage response;
        try
        {
            response = await PostFormAsync("connect/token", form, ct);
        }
        catch
        {
            await _store.ClearAsync(ct);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            await _store.ClearAsync(ct);
            return false;
        }

        var username = await _store.GetUsernameAsync(ct) ?? "";
        var token = await ParseTokenAsync(response, username, ct);
        await _store.SaveAsync(token, ct);
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

    /// <summary>POSTs a form to a relative endpoint on the plain (no-auth) client.
    /// Adds the <c>__tenant</c> HEADER (mirrors AlphaAgent's TenantHeaderDelegatingHandler on its
    /// authenticated login client) so ABP resolves the tenant on the token endpoint and issues a
    /// tenant-scoped JWT — form-field alone is unreliable for refresh_token grant.
    /// Throws on transport failure — callers handle (login surfaces the message, refresh returns false).</summary>
    private async Task<HttpResponseMessage> PostFormAsync(string endpoint, IDictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (!string.IsNullOrWhiteSpace(_options.Tenant))
            request.Headers.Add("__tenant", _options.Tenant);
        return await _http.SendAsync(request, ct);
    }
}
