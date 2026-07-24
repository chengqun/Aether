using Acme.Llm.Domain;
using Acme.Platform.Domain;

namespace Acme.Llm.Application;

/// <summary>
/// LLM config application service. Ported from AlphaAgent's
/// <c>LlmConfigService</c>, adapted to Platform's <see cref="IAppServerHttpClient"/>
/// (authenticated sync). The local device is single-user (the logged-in app
/// user), so no user id is stored or filtered — the server identifies the user
/// via the bearer token on sync.
/// </summary>
public sealed class LlmConfigService : ILlmConfigService
{
    private readonly IAppServerHttpClient _http;
    private readonly ILlmConfigRepository _repository;

    public LlmConfigService(IAppServerHttpClient http, ILlmConfigRepository repository)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc/>
    public async Task<List<LlmConfigDto>?> GetCachedAsync(CancellationToken ct = default)
    {
        var items = await _repository.GetAllAsync(ct);
        if (items.Count == 0) return null;
        return items.Select(MapToDto).ToList();
    }

    /// <inheritdoc/>
    public async Task<List<LlmConfigDto>?> SyncFromServerAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetAsync<ListResult<LlmConfigDto>>("api/app/llm-config/my-llm-configs", ct);
            if (result?.Items is null || result.Items.Count == 0)
                return await GetCachedAsync(ct);

            var cacheItems = result.Items.Select(MapToConfig).ToList();

            // Full replace: clear local, then write fresh from server.
            await _repository.ClearAsync(ct);
            await _repository.UpsertRangeAsync(cacheItems, ct);

            return result.Items;
        }
        catch
        {
            // Network/server failure — fall back to the local cache.
            return await GetCachedAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task SetConfigAsync(LlmConfigDto config, CancellationToken ct = default)
    {
        if (config.Id == Guid.Empty) config.Id = Guid.NewGuid();
        var item = MapToConfig(config);

        // Default is mutually exclusive: clear IsDefault on the user's other (non-system) configs.
        if (config.IsDefault)
        {
            var all = await _repository.GetAllAsync(ct);
            var changed = all.Where(o => !o.IsSystem && o.Id != item.Id && o.IsDefault).ToList();
            foreach (var o in changed) o.IsDefault = false;
            if (changed.Count > 0) await _repository.UpsertRangeAsync(changed, ct);
        }

        item.CachedAt = DateTimeOffset.UtcNow;
        await _repository.UpsertRangeAsync(new[] { item }, ct);
    }

    private static LlmConfig MapToConfig(LlmConfigDto dto) => new()
    {
        Id = dto.Id != Guid.Empty ? dto.Id : Guid.NewGuid(),
        Name = dto.Name,
        ModelName = dto.ModelName,
        ApiKey = dto.ApiKey,
        Endpoint = dto.Endpoint,
        Temperature = dto.Temperature,
        IsDefault = dto.IsDefault,
        IsSystem = dto.IsSystem,
        CachedAt = DateTimeOffset.UtcNow
    };

    private static LlmConfigDto MapToDto(LlmConfig item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        ModelName = item.ModelName,
        ApiKey = item.ApiKey,
        Endpoint = item.Endpoint,
        Temperature = item.Temperature,
        IsDefault = item.IsDefault,
        IsSystem = item.IsSystem
    };

    /// <summary>Matches the server's paged result envelope (<c>{ items: [...] }</c>).</summary>
    private sealed class ListResult<T>
    {
        public List<T> Items { get; set; } = new();
    }
}
