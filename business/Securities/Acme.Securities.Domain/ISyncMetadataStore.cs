namespace Acme.Securities.Domain;

/// <summary>
/// Stores the last-success time of a sync operation, keyed by name. Used by the
/// security client sync to pull incremental updates. Replaces AlphaAgent's
/// <c>ISyncMetadataStore</c> (which was in the shared kernel).
/// </summary>
public interface ISyncMetadataStore
{
    Task<DateTime?> GetLastSyncTimeAsync(string key, CancellationToken ct = default);
    Task SetLastSyncTimeAsync(string key, DateTime time, CancellationToken ct = default);
}
