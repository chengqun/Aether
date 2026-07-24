namespace Acme.Platform.Application.Auth;

/// <summary>Result of a login/refresh attempt.</summary>
public sealed class LoginResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? AccessToken { get; init; }
}

/// <summary>
/// App-server authentication use case: log in (OAuth2 password grant), refresh,
/// logout. Acquired tokens are persisted via <see cref="Acme.Platform.Domain.IAppTokenStore"/>
/// so the session survives restarts; subsequent app-server calls are
/// authenticated automatically via the bearer HTTP client.
/// </summary>
public interface IAppAuthService
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<bool> RefreshAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<bool> IsLoggedInAsync(CancellationToken ct = default);
}
