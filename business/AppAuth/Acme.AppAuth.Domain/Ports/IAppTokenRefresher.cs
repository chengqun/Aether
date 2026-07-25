namespace Acme.AppAuth.Domain;

/// <summary>
/// Refreshes the app-server access token using the stored refresh token.
/// Implemented by the auth service and consumed by the bearer HTTP handler on
/// 401. Defined in Domain so the Infrastructure-layer handler does not need to
/// reference the Application layer.
/// </summary>
public interface IAppTokenRefresher
{
    Task<bool> RefreshAsync(CancellationToken ct = default);
}
