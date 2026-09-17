using Acme.Desktop.Mvvm;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// ChatLlmSelectionService（ChatVM 拆分 B2）单测：LLM 选中解析链（saved → IsDefault → first）、
/// holder 同步、选中变化的持久化 + invalidate 委托语义。
/// </summary>
public class ChatLlmSelectionServiceTests
{
    private sealed class FakeLlmService : ILlmConfigService
    {
        public List<LlmConfigDto>? Configs { get; set; }
        public Task<List<LlmConfigDto>?> GetCachedAsync(CancellationToken ct = default)
            => Task.FromResult(Configs);
        public Task<List<LlmConfigDto>?> SyncFromServerAsync(CancellationToken ct = default)
            => Task.FromResult(Configs);
        public Task SetConfigAsync(LlmConfigDto config, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakePrefs : IPreferencesStore
    {
        public Dictionary<string, string?> Values { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private static LlmConfigDto Cfg(Guid id, bool isDefault = false) => new() { Id = id, Name = $"llm-{id:N}"[..12], IsDefault = isDefault };

    [Fact]
    public async Task LoadAsync_Restores_Saved_Selection_First()
    {
        var saved = Guid.NewGuid();
        var configs = new List<LlmConfigDto> { Cfg(Guid.NewGuid(), isDefault: true), Cfg(saved), Cfg(Guid.NewGuid()) };
        var prefs = new FakePrefs { Values = { ["selectedLlmId"] = saved.ToString() } };
        var service = new ChatLlmSelectionService(new FakeLlmService { Configs = configs }, prefs, null, null);

        var (list, selected) = await service.LoadAsync();

        // LoadAsync 返回防御性拷贝（ToList）——内容相等即可
        Assert.Equal(configs, list);
        Assert.Equal(saved, selected!.Id);
    }

    [Fact]
    public async Task LoadAsync_Falls_Back_To_Default_Then_First()
    {
        var def = Guid.NewGuid();
        var configs = new List<LlmConfigDto> { Cfg(Guid.NewGuid()), Cfg(def, isDefault: true) };
        var noSaved = new ChatLlmSelectionService(new FakeLlmService { Configs = configs }, new FakePrefs(), null, null);

        var (_, selectedDefault) = await noSaved.LoadAsync();
        Assert.Equal(def, selectedDefault!.Id);

        var empty = new List<LlmConfigDto> { Cfg(Guid.NewGuid()) };
        var onlyFirst = new ChatLlmSelectionService(new FakeLlmService { Configs = empty }, new FakePrefs(), null, null);
        var (_, selectedFirst) = await onlyFirst.LoadAsync();
        Assert.Equal(empty[0].Id, selectedFirst!.Id);
    }

    [Fact]
    public async Task LoadAsync_Syncs_Holder_When_Selected()
    {
        var configs = new List<LlmConfigDto> { Cfg(Guid.NewGuid(), isDefault: true) };
        var holder = new Aether.AI.Harness.SelectedLlmHolder();
        var service = new ChatLlmSelectionService(new FakeLlmService { Configs = configs }, new FakePrefs(), holder, null);

        var (_, selected) = await service.LoadAsync();

        Assert.NotNull(selected);
        Assert.Equal(selected.Id.ToString(), holder.LlmConfigId);
    }

    [Fact]
    public async Task LoadAsync_Returns_Null_On_Failure_Or_Empty_Configs()
    {
        var service = new ChatLlmSelectionService(new FakeLlmService { Configs = null }, new FakePrefs(), null, null);
        var (configs, selected) = await service.LoadAsync();
        Assert.Null(configs);
        Assert.Null(selected);

        var empty = new ChatLlmSelectionService(new FakeLlmService { Configs = new List<LlmConfigDto>() }, new FakePrefs(), null, null);
        var (list2, selected2) = await empty.LoadAsync();
        Assert.NotNull(list2);
        Assert.Empty(list2!);
        Assert.Null(selected2);
    }

    [Fact]
    public async Task OnSelectionChanged_Persists_And_Syncs_Holder_And_Invalidates()
    {
        var prefs = new FakePrefs();
        var holder = new Aether.AI.Harness.SelectedLlmHolder();
        var invalidated = new List<Guid>();
        var cfg = Cfg(Guid.NewGuid());
        var service = new ChatLlmSelectionService(new FakeLlmService(), prefs, holder, () => invalidated.Add(Guid.NewGuid()));

        service.OnSelectionChanged(cfg);
        // persist 是 fire-and-forget——轮询等落盘（同步完成的 FakePrefs 实际即刻完成）
        Assert.Equal(cfg.Id.ToString(), prefs.Values["selectedLlmId"]);
        Assert.Equal(cfg.Id.ToString(), holder.LlmConfigId);
        Assert.Single(invalidated);
    }

    [Fact]
    public void OnSelectionChanged_Null_Value_Is_NoOp()
    {
        var prefs = new FakePrefs();
        var invalidated = 0;
        var service = new ChatLlmSelectionService(new FakeLlmService(), prefs, new Aether.AI.Harness.SelectedLlmHolder(), () => invalidated++);

        service.OnSelectionChanged(null);

        Assert.Empty(prefs.Values);
        Assert.Equal(0, invalidated);
    }

    [Fact]
    public async Task OnSelectionChanged_Without_Holder_Persists_But_Does_Not_Invalidate()
    {
        var prefs = new FakePrefs();
        var invalidated = 0;
        var cfg = Cfg(Guid.NewGuid());
        var service = new ChatLlmSelectionService(new FakeLlmService(), prefs, null, () => invalidated++);

        service.OnSelectionChanged(cfg);

        Assert.Equal(cfg.Id.ToString(), prefs.Values["selectedLlmId"]);
        Assert.Equal(0, invalidated); // holder 未解析 → 不 invalidate（原语义）
        await Task.CompletedTask;
    }
}
