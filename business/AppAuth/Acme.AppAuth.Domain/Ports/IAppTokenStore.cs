namespace Acme.AppAuth.Domain;

/// <summary>
/// An app-server session token (OAuth2 access + refresh), cached locally so the
/// user stays logged in across restarts.
/// </summary>
public sealed class AppToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Persistent store for the current app-server session token. Replaces
/// AlphaAgent's <c>ITokenManager</c> + <c>ITokenRepository</c> (EF Core SQLite).
/// A concrete store (SQLite) is provided by Platform.Infrastructure.
/// </summary>
public interface IAppTokenStore
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
    Task<string?> GetRefreshTokenAsync(CancellationToken ct = default);
    Task<string?> GetUsernameAsync(CancellationToken ct = default);
    Task<DateTimeOffset?> GetExpiresAtAsync(CancellationToken ct = default);
    Task SaveAsync(AppToken token, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
