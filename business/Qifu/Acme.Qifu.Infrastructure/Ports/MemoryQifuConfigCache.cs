using System.Collections.Concurrent;
using Acme.Qifu.Domain.Ports;

namespace Acme.Qifu.Infrastructure.Ports;

/// <summary>
/// In-process <see cref="IQifuConfigCache"/>. Default implementation for the
/// skeleton; entries are lost on restart. A file- or SQLite-backed cache should
/// replace this for production (the qifu bounded context declares only the port).
/// </summary>
public sealed class MemoryQifuConfigCache : IQifuConfigCache
{
    private readonly ConcurrentDictionary<string, QifuConfigCacheItem> _items = new();

    private static string Key(string userId, string configKey) => $"{userId}::{configKey}";

    public Task<QifuConfigCacheItem?> GetAsync(string userId, string configKey, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(Key(userId, configKey), out var item) ? item : null);

    public Task UpsertAsync(QifuConfigCacheItem item, CancellationToken ct = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        item.CachedAt = DateTimeOffset.UtcNow;
        _items[Key(item.UserId, item.ConfigKey)] = item;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string userId, string configKey, CancellationToken ct = default)
    {
        _items.TryRemove(Key(userId, configKey), out _);
        return Task.CompletedTask;
    }
}
