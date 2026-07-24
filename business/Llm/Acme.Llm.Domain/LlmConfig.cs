namespace Acme.Llm.Domain;

/// <summary>
/// An LLM provider configuration (DeepSeek, GPT-4o, local model, …). Persisted
/// to local SQLite; pulled from the app server. The local device is single-user
/// — the owning user is whoever is logged into the app (resolved via the
/// Platform context's <c>ICurrentUser</c>), so no per-user column is stored
/// locally. Server sync is authenticated (bearer), so the server also knows the
/// user without a local column. Ported from AlphaAgent's <c>LlmConfigCacheItem</c>.
/// </summary>
public sealed class LlmConfig
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ModelName { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public float Temperature { get; set; } = 0.5f;
    public bool IsDefault { get; set; }

    /// <summary>System configs are server-managed (shared); user configs are created locally.</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}
