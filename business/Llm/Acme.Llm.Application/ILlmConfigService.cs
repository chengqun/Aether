using Acme.Llm.Domain;

namespace Acme.Llm.Application;

/// <summary>
/// Manages LLM provider configs: read local cache, pull from the app server,
/// save locally. The current user is resolved internally via Platform's
/// <c>ICurrentUser</c>. Ported from AlphaAgent's <c>ILlmConfigService</c>.
/// </summary>
public interface ILlmConfigService
{
    /// <summary>Returns the user's visible configs (own + system) from the local cache, or null if none.</summary>
    Task<List<LlmConfigDto>?> GetCachedAsync(CancellationToken ct = default);

    /// <summary>Pulls configs from the app server (authenticated) and full-replaces the local cache.</summary>
    Task<List<LlmConfigDto>?> SyncFromServerAsync(CancellationToken ct = default);

    /// <summary>Saves a config locally (default-mutual-exclusive). Does NOT push to the server.</summary>
    Task SetConfigAsync(LlmConfigDto config, CancellationToken ct = default);
}
