using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;

namespace Acme.Llm.Infrastructure.Tests;

public class SqliteLlmConfigRepositoryTests
{
    [Fact]
    public async Task Upsert_then_GetAll_round_trips()
    {
        await using var repo = await CreateAsync();
        await repo.UpsertRangeAsync(new[]
        {
            NewConfig("deepseek", isSystem: false),
            NewConfig("system-default", isSystem: true)
        });

        var all = await repo.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.Name == "deepseek" && !c.IsSystem);
        Assert.Contains(all, c => c.Name == "system-default" && c.IsSystem);
    }

    [Fact]
    public async Task Clear_empties_the_table()
    {
        await using var repo = await CreateAsync();
        await repo.UpsertRangeAsync(new[] { NewConfig("a"), NewConfig("b") });

        await repo.ClearAsync();

        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Upsert_replaces_same_id()
    {
        await using var repo = await CreateAsync();
        var id = Guid.NewGuid();
        await repo.UpsertRangeAsync(new[] { NewConfig("old", id: id) });
        await repo.UpsertRangeAsync(new[] { NewConfig("new", id: id) });

        var all = await repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("new", all[0].Name);
    }

    [Fact]
    public async Task Fields_round_trip()
    {
        await using var repo = await CreateAsync();
        var cfg = new LlmConfig
        {
            Id = Guid.NewGuid(),
            Name = "ds",
            ModelName = "deepseek-chat",
            ApiKey = "sk-test",
            Endpoint = "https://api.deepseek.com",
            Temperature = 0.7f,
            IsDefault = true,
            IsSystem = false
        };
        await repo.UpsertRangeAsync(new[] { cfg });

        var loaded = (await repo.GetAllAsync())[0];
        Assert.Equal(cfg.ModelName, loaded.ModelName);
        Assert.Equal(cfg.ApiKey, loaded.ApiKey);
        Assert.Equal(cfg.Endpoint, loaded.Endpoint);
        Assert.Equal(cfg.Temperature, loaded.Temperature);
        Assert.True(loaded.IsDefault);
        Assert.False(loaded.IsSystem);
    }

    private static LlmConfig NewConfig(string name, bool isSystem = false, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        ModelName = "deepseek-v4-flash",
        ApiKey = "sk-test",
        Endpoint = "https://api.deepseek.com",
        Temperature = 0.5f,
        IsDefault = false,
        IsSystem = isSystem
    };

    private static async Task<TempFileRepo> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}.db");
        var repo = SqliteLlmConfigRepository.ForFile(path);
        await repo.GetAllAsync(); // force schema creation
        return new TempFileRepo(repo, path);
    }

    private sealed class TempFileRepo : ILlmConfigRepository, IAsyncDisposable
    {
        private readonly ILlmConfigRepository _inner;
        private readonly string _path;
        public TempFileRepo(ILlmConfigRepository inner, string path) { _inner = inner; _path = path; }
        public Task<List<LlmConfig>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);
        public Task UpsertRangeAsync(IEnumerable<LlmConfig> items, CancellationToken ct = default) => _inner.UpsertRangeAsync(items, ct);
        public Task ClearAsync(CancellationToken ct = default) => _inner.ClearAsync(ct);
        public ValueTask DisposeAsync() { try { File.Delete(_path); } catch { } return ValueTask.CompletedTask; }
    }
}
