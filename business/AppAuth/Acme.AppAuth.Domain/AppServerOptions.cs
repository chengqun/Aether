namespace Acme.AppAuth.Domain;

/// <summary>
/// Configuration for the Platform (app-server) context: the ABP server base
/// address, OAuth2 client credentials, and the local SQLite path for the
/// session token cache. Resolved by the host from configuration / env vars.
/// </summary>
public sealed class AppServerOptions
{
    /// <summary>App server base URL, e.g. <c>https://app.example.com/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>OAuth2 client_id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client_secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth2 scope (optional).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>ABP tenant name (sent as <c>__tenant</c> on the token endpoint). Empty = host tenant.</summary>
    public string Tenant { get; set; } = string.Empty;

    /// <summary>SQLite file for the session token cache; null falls back to <c>~/.aether/platform.db</c>.</summary>
    public string? DbPath { get; set; }
}
