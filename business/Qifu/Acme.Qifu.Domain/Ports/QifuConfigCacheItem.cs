namespace Acme.Qifu.Domain.Ports;

/// <summary>
/// A cached config entry for qifu (a token, sid, or client config), keyed by
/// user + config key, with JSON-serialized content. Mirrors AlphaAgent's
/// <c>YushuConfigCacheItem</c> / <c>CloudDocsConfigCacheItem</c> but uses a
/// string user id and lives in the qifu bounded context (no EF/SQLite dependency).
/// </summary>
public sealed class QifuConfigCacheItem
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>Config key, e.g. "token", "client_config", "sid".</summary>
    public string ConfigKey { get; set; } = string.Empty;

    /// <summary>JSON-serialized config content.</summary>
    public string ConfigJson { get; set; } = string.Empty;

    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}
