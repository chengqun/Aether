using System.Collections.Concurrent;
using Acme.Qifu.Domain.Ports;

namespace Acme.Qifu.Infrastructure.Ports;

/// <summary>
/// In-process <see cref="IQifuConfigCache"/>（单用户 key-value）。
/// </summary>
public sealed class MemoryQifuConfigCache : IQifuConfigCache
{
    private readonly ConcurrentDictionary<string, QifuConfigCacheItem> _items = new();

    public Task<QifuConfigCacheItem?> GetAsync(string configKey, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(configKey, out var item) ? item : null);

    public Task UpsertAsync(string configKey, string configJson, CancellationToken ct = default)
    {
        _items[configKey] = new QifuConfigCacheItem { ConfigKey = configKey, ConfigJson = configJson, CachedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string configKey, CancellationToken ct = default)
    {
        _items.TryRemove(configKey, out _);
        return Task.CompletedTask;
    }
}
