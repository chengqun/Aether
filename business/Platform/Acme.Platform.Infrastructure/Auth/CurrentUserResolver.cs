using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acme.Platform.Domain;

namespace Acme.Platform.Infrastructure.Auth;

/// <summary>
/// Resolves the current user id from the cached app-server access token's JWT
/// <c>sub</c> claim. Falls back to an MD5 of the username if the JWT can't be
/// parsed. Ported from AlphaAgent's <c>UserIdResolver</c>, adapted to
/// <see cref="IAppTokenStore"/> + returning a string id.
/// </summary>
public sealed class CurrentUserResolver : ICurrentUser
{
    private readonly IAppTokenStore _tokenStore;

    public CurrentUserResolver(IAppTokenStore tokenStore)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    /// <inheritdoc/>
    public async Task<string?> GetCurrentUserIdAsync(CancellationToken ct = default)
    {
        var token = await _tokenStore.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                var payload = token.Split('.')[1];
                var jsonBytes = Convert.FromBase64String(PadBase64(payload));
                using var doc = JsonDocument.Parse(jsonBytes);
                if (doc.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String)
                {
                    var value = sub.GetString();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch
            {
                // fall through to username-based fallback
            }
        }

        var username = await _tokenStore.GetUsernameAsync(ct);
        return string.IsNullOrEmpty(username) ? null : Md5String(username);
    }

    private static string PadBase64(string base64)
        => base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

    private static string Md5String(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString();
    }
}
