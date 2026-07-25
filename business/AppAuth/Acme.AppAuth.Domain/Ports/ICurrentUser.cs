namespace Acme.AppAuth.Domain;

/// <summary>
/// Resolves the current application user identifier. This is the app-server
/// (ABP) user identity, NOT any qifu platform identity — it is the user logged
/// into the enterprise app backend. Consumed by bounded contexts that need to
/// scope data per user (e.g. qifu's token cache).
/// </summary>
public interface ICurrentUser
{
    /// <summary>The current user id, or null if not logged in.</summary>
    Task<string?> GetCurrentUserIdAsync(CancellationToken ct = default);
}
