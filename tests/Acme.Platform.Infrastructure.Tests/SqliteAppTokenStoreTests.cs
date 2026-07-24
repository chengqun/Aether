using Acme.Platform.Domain;
using Acme.Platform.Infrastructure.Persistence;

namespace Acme.Platform.Infrastructure.Tests;

public class SqliteAppTokenStoreTests
{
    [Fact]
    public async Task Save_then_read_round_trips()
    {
        await using var store = await CreateAsync();
        var token = new AppToken
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            Username = "alice",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await store.SaveAsync(token);

        Assert.Equal("access-123", await store.GetAccessTokenAsync());
        Assert.Equal("refresh-456", await store.GetRefreshTokenAsync());
        Assert.Equal("alice", await store.GetUsernameAsync());
        var exp = await store.GetExpiresAtAsync();
        Assert.NotNull(exp);
        Assert.True(exp > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Save_overwrites_previous_session()
    {
        await using var store = await CreateAsync();
        await store.SaveAsync(new AppToken { AccessToken = "old", RefreshToken = "r", Username = "alice", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await store.SaveAsync(new AppToken { AccessToken = "new", RefreshToken = "r2", Username = "alice", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) });

        Assert.Equal("new", await store.GetAccessTokenAsync());
        Assert.Equal("r2", await store.GetRefreshTokenAsync());
    }

    [Fact]
    public async Task Clear_removes_session()
    {
        await using var store = await CreateAsync();
        await store.SaveAsync(new AppToken { AccessToken = "x", RefreshToken = "y", Username = "u", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        await store.ClearAsync();

        Assert.Null(await store.GetAccessTokenAsync());
        Assert.Null(await store.GetUsernameAsync());
    }

    [Fact]
    public async Task Empty_store_returns_nulls()
    {
        await using var store = await CreateAsync();
        Assert.Null(await store.GetAccessTokenAsync());
        Assert.Null(await store.GetRefreshTokenAsync());
        Assert.Null(await store.GetUsernameAsync());
        Assert.Null(await store.GetExpiresAtAsync());
    }

    private static async Task<TempFileStore> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-test-{Guid.NewGuid():N}.db");
        var store = SqliteAppTokenStore.ForFile(path);
        await store.GetAccessTokenAsync(); // warm up schema
        return new TempFileStore(store, path);
    }

    private sealed class TempFileStore : IAppTokenStore, IAsyncDisposable
    {
        private readonly IAppTokenStore _inner;
        private readonly string _path;
        public TempFileStore(IAppTokenStore inner, string path) { _inner = inner; _path = path; }
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => _inner.GetAccessTokenAsync(ct);
        public Task<string?> GetRefreshTokenAsync(CancellationToken ct = default) => _inner.GetRefreshTokenAsync(ct);
        public Task<string?> GetUsernameAsync(CancellationToken ct = default) => _inner.GetUsernameAsync(ct);
        public Task<DateTimeOffset?> GetExpiresAtAsync(CancellationToken ct = default) => _inner.GetExpiresAtAsync(ct);
        public Task SaveAsync(AppToken token, CancellationToken ct = default) => _inner.SaveAsync(token, ct);
        public Task ClearAsync(CancellationToken ct = default) => _inner.ClearAsync(ct);
        public ValueTask DisposeAsync() { try { File.Delete(_path); } catch { } return ValueTask.CompletedTask; }
    }
}
