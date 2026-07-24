namespace Acme.Qifu.Domain.Ports;

/// <summary>
/// qifu config/token cache store. Replaces AlphaAgent's
/// <c>IYushuConfigCacheRepository</c> / <c>ICloudDocsConfigCacheRepository</c>
/// (which were backed by an EF Core SQLite DbContext). A concrete store
/// (in-memory, file, SQLite) is provided by Infrastructure.
/// </summary>
public interface IQifuConfigCache
{
    /// <summary>Gets a cached entry for the given user + key, or null if absent.</summary>
    Task<QifuConfigCacheItem?> GetAsync(string userId, string configKey, CancellationToken ct = default);

    /// <summary>Inserts or updates a cached entry.</summary>
    Task UpsertAsync(QifuConfigCacheItem item, CancellationToken ct = default);

    /// <summary>Deletes a cached entry. Returns true if an entry was removed.</summary>
    Task DeleteAsync(string userId, string configKey, CancellationToken ct = default);
}
