using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Infrastructure.Persistence;

namespace Acme.Qifu.Infrastructure.Tests;

public class SqliteQifuConfigCacheTests
{
    [Fact]
    public async Task Upsert_then_Get_round_trips()
    {
        await using var cache = await CreateAsync();
        var item = new QifuConfigCacheItem
        {
            UserId = "u1",
            ConfigKey = "token",
            ConfigJson = """{"access_token":"abc","expires_at":123}"""
        };

        await cache.UpsertAsync(item);
        var loaded = await cache.GetAsync("u1", "token");

        Assert.NotNull(loaded);
        Assert.Equal("u1", loaded!.UserId);
        Assert.Equal("token", loaded.ConfigKey);
        Assert.Contains("access_token", loaded.ConfigJson);
    }

    [Fact]
    public async Task Upsert_overwrites_existing_value()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u1", ConfigKey = "sid", ConfigJson = "old" });
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u1", ConfigKey = "sid", ConfigJson = "new" });

        var loaded = await cache.GetAsync("u1", "sid");
        Assert.NotNull(loaded);
        Assert.Equal("new", loaded!.ConfigJson);
    }

    [Fact]
    public async Task Get_returns_null_when_absent()
    {
        await using var cache = await CreateAsync();
        var loaded = await cache.GetAsync("nobody", "missing");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Delete_removes_entry()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u1", ConfigKey = "sid", ConfigJson = "x" });

        await cache.DeleteAsync("u1", "sid");

        Assert.Null(await cache.GetAsync("u1", "sid"));
    }

    [Fact]
    public async Task Entries_are_isolated_per_user_and_key()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u1", ConfigKey = "token", ConfigJson = "a" });
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u2", ConfigKey = "token", ConfigJson = "b" });
        await cache.UpsertAsync(new QifuConfigCacheItem { UserId = "u1", ConfigKey = "sid", ConfigJson = "c" });

        Assert.Equal("a", (await cache.GetAsync("u1", "token"))!.ConfigJson);
        Assert.Equal("b", (await cache.GetAsync("u2", "token"))!.ConfigJson);
        Assert.Equal("c", (await cache.GetAsync("u1", "sid"))!.ConfigJson);
    }

    /// <summary>Creates a cache backed by a unique temp file, and disposes (deletes) it afterward.</summary>
    private static async Task<TempFileCache> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qifu-test-{Guid.NewGuid():N}.db");
        var cache = SqliteQifuConfigCache.ForFile(path);
        // Force schema creation up front.
        await cache.GetAsync("__warmup__", "__warmup__");
        return new TempFileCache(cache, path);
    }

    private sealed class TempFileCache : IQifuConfigCache, IAsyncDisposable
    {
        private readonly IQifuConfigCache _inner;
        private readonly string _path;

        public TempFileCache(IQifuConfigCache inner, string path) { _inner = inner; _path = path; }
        public Task<QifuConfigCacheItem?> GetAsync(string userId, string configKey, CancellationToken ct = default) => _inner.GetAsync(userId, configKey, ct);
        public Task UpsertAsync(QifuConfigCacheItem item, CancellationToken ct = default) => _inner.UpsertAsync(item, ct);
        public Task DeleteAsync(string userId, string configKey, CancellationToken ct = default) => _inner.DeleteAsync(userId, configKey, ct);
        public async ValueTask DisposeAsync()
        {
            try { File.Delete(_path); } catch { /* best effort */ }
            await Task.CompletedTask;
        }
    }
}
