namespace Acme.Llm.Domain;

/// <summary>
/// Local persistence port for <see cref="LlmConfig"/>. Replaces AlphaAgent's
/// <c>ILlmConfigCacheRepository</c> (EF Core SQLite). The local device is
/// single-user, so there is no per-user filtering — all local configs belong to
/// the logged-in user. A concrete SQLite store is provided by Infrastructure.
/// </summary>
public interface ILlmConfigRepository
{
    /// <summary>All local LLM configs (the logged-in user's + system configs).</summary>
    Task<List<LlmConfig>> GetAllAsync(CancellationToken ct = default);

    Task UpsertRangeAsync(IEnumerable<LlmConfig> items, CancellationToken ct = default);

    /// <summary>Deletes all local configs (used before a full re-sync from server).</summary>
    Task ClearAsync(CancellationToken ct = default);
}
