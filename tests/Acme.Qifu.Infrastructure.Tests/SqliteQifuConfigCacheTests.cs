using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Infrastructure.Persistence;

namespace Acme.Qifu.Infrastructure.Tests;

public class SqliteQifuConfigCacheTests
{
    [Fact]
    public async Task Upsert_then_Get_round_trips()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync("token", """{"access_token":"abc","expires_at":123}""");

        var loaded = await cache.GetAsync("token");

        Assert.NotNull(loaded);
        Assert.Equal("token", loaded!.ConfigKey);
        Assert.Contains("access_token", loaded.ConfigJson);
    }

    [Fact]
    public async Task Upsert_overwrites_existing_value()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync("sid", "old");
        await cache.UpsertAsync("sid", "new");

        var loaded = await cache.GetAsync("sid");
        Assert.NotNull(loaded);
        Assert.Equal("new", loaded!.ConfigJson);
    }

    [Fact]
    public async Task Get_returns_null_when_absent()
    {
        await using var cache = await CreateAsync();
        var loaded = await cache.GetAsync("missing");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Delete_removes_entry()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync("sid", "x");

        await cache.DeleteAsync("sid");

        Assert.Null(await cache.GetAsync("sid"));
    }

    [Fact]
    public async Task Entries_are_isolated_per_key()
    {
        await using var cache = await CreateAsync();
        await cache.UpsertAsync("token", "a");
        await cache.UpsertAsync("sid", "b");

        Assert.Equal("a", (await cache.GetAsync("token"))!.ConfigJson);
        Assert.Equal("b", (await cache.GetAsync("sid"))!.ConfigJson);
    }

    /// <summary>Creates a cache backed by a unique temp file, and disposes (deletes) it afterward.</summary>
    private static async Task<TempFileCache> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qifu-test-{Guid.NewGuid():N}.db");
        var cache = SqliteQifuConfigCache.ForFile(path);
        // Force schema creation up front.
        await cache.GetAsync("__warmup__");
        return new TempFileCache(cache, path);
    }

    private sealed class TempFileCache : IQifuConfigCache, IAsyncDisposable
    {
        private readonly IQifuConfigCache _inner;
        private readonly string _path;

        public TempFileCache(IQifuConfigCache inner, string path) { _inner = inner; _path = path; }
        public Task<QifuConfigCacheItem?> GetAsync(string configKey, CancellationToken ct = default) => _inner.GetAsync(configKey, ct);
        public Task UpsertAsync(string configKey, string configJson, CancellationToken ct = default) => _inner.UpsertAsync(configKey, configJson, ct);
        public Task DeleteAsync(string configKey, CancellationToken ct = default) => _inner.DeleteAsync(configKey, ct);
        public async ValueTask DisposeAsync()
        {
            try { File.Delete(_path); } catch { /* best effort */ }
            await Task.CompletedTask;
        }
    }
}
