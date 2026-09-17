using Acme.Desktop.Mvvm;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// ChatTargetRegistry（ChatVM 拆分 B3）单测：目标池构建 / (Id,Kind) 逻辑键 / chatTargets 偏好 round-trip
/// （含 legacy selectedAgentId 迁移）/ 恢复态解析到池中实例。三个宿主 VM 经 stub 依赖真实构造（集合直接填充）。
/// </summary>
public class ChatTargetRegistryTests
{
    // —— stub 依赖（真实构造 Experts/Workflows/Rules 三个 VM 用） ——

    private sealed class FakeDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private sealed class StubAgentCatalog : ICustomAgentCatalog
    {
        public Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CustomAgentDefinition>>(Array.Empty<CustomAgentDefinition>());
        public Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<CustomAgentDefinition?>(null);
        public Task<CustomAgentDefinition?> GetByMarketplaceIdAsync(Guid marketplaceId, CancellationToken ct = default) => Task.FromResult<CustomAgentDefinition?>(null);
        public Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<CustomToolMeta> GetAvailableTools() => Array.Empty<CustomToolMeta>();
        public string CurrentHost => "test";
        public IReadOnlyList<CustomModelOption> GetAvailableModels() => Array.Empty<CustomModelOption>();
    }

    private sealed class StubAgentRunner : ICustomAgentRunner
    {
        public Task RunStreamingAsync(Guid conversationId, Guid agentId, string message, StreamHandlers? handlers = null,
            ToolApprovalDecision? approvalResponse = null, string? desiredMode = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Invalidate(Guid agentId) { }
        public void EvictConversation(Guid conversationId) { }
    }

    private sealed class StubWorkflowCatalog : ICustomWorkflowCatalog
    {
        public Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CustomWorkflowDefinition>>(Array.Empty<CustomWorkflowDefinition>());
        public Task<CustomWorkflowDefinition?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<CustomWorkflowDefinition?>(null);
        public Task SaveAsync(CustomWorkflowDefinition definition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubWorkflowRunner : ICustomWorkflowRunner
    {
        public Task RunStreamingAsync(Guid conversationId, Guid workflowId, string message, StreamHandlers? handlers = null,
            ToolApprovalDecision? approvalResponse = null, string? desiredMode = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Invalidate(Guid workflowId) { }
        public void EvictConversation(Guid conversationId) { }
    }

    private sealed class StubMcpManagement : IMcpManagementService
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpServerStatus>> GetServersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpServerStatus>>(Array.Empty<McpServerStatus>());
        public Task SaveServerAsync(McpServerConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteServerAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartHealthMonitoringAsync(TimeSpan interval, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private static (ExpertsViewModel Experts, WorkflowsViewModel Workflows, RulesViewModel Rules) NewVms()
    {
        var disp = new FakeDispatcher();
        var mcp = new McpViewModel(new StubMcpManagement(), disp);
        var experts = new ExpertsViewModel(new StubAgentCatalog(), new StubAgentRunner(), disp, mcp);
        var workflows = new WorkflowsViewModel(new StubWorkflowCatalog(), new StubAgentCatalog(), new StubWorkflowRunner(), disp);
        var rules = new RulesViewModel(new StubAgentCatalog(), new StubAgentRunner(), disp, mcp);
        return (experts, workflows, rules);
    }

    private static ChatTargetRegistry NewRegistry(
        out ExpertsViewModel experts, out WorkflowsViewModel workflows, out RulesViewModel rules, IPreferencesStore? prefs = null)
    {
        (experts, workflows, rules) = NewVms();
        return new ChatTargetRegistry(experts, workflows, rules, prefs ?? new FakePrefs());
    }

    // —— KindOf / TargetKey ——

    [Fact]
    public void KindOf_Distinguishes_Workflow_Rule_Agent_None()
    {
        var agent = new ExpertItem { Id = Guid.NewGuid(), NodeType = NoteNodeType.Agent };
        var rule = new ExpertItem { Id = Guid.NewGuid(), NodeType = NoteNodeType.Rule };
        var wf = new WorkflowItem { Id = Guid.NewGuid() };

        Assert.Equal(ChatTargetKind.Agent, ChatTargetRegistry.KindOf(agent));
        Assert.Equal(ChatTargetKind.Rule, ChatTargetRegistry.KindOf(rule));
        Assert.Equal(ChatTargetKind.Workflow, ChatTargetRegistry.KindOf(wf));
        Assert.Equal(ChatTargetKind.None, ChatTargetRegistry.KindOf(null));
        Assert.Equal(ChatTargetKind.None, ChatTargetRegistry.KindOf("not a target"));
    }

    [Fact]
    public void TargetKey_Uses_Id_And_Kind()
    {
        var id = Guid.NewGuid();
        var agent = new ExpertItem { Id = id, NodeType = NoteNodeType.Agent };
        var wf = new WorkflowItem { Id = id };

        Assert.Equal((id, ChatTargetKind.Agent), ChatTargetRegistry.TargetKey(agent, ChatTargetKind.Agent));
        Assert.Equal((id, ChatTargetKind.Workflow), ChatTargetRegistry.TargetKey(wf, ChatTargetKind.None));
        Assert.Equal((Guid.Empty, ChatTargetKind.None), ChatTargetRegistry.TargetKey(null, ChatTargetKind.Agent));
    }

    // —— BuildPool ——

    [Fact]
    public void BuildPool_Lists_All_Experts_Rules_Workflows()
    {
        var registry = NewRegistry(out var experts, out var workflows, out var rules);
        experts.Experts.Add(new ExpertItem { Id = Guid.NewGuid(), Name = "a1" });
        rules.Rules.Add(new ExpertItem { Id = Guid.NewGuid(), Name = "r1", NodeType = NoteNodeType.Rule });
        workflows.Workflows.Add(new WorkflowItem { Id = Guid.NewGuid(), Name = "w1" });

        var pool = registry.BuildPool();

        Assert.Equal(3, pool.Count);
        Assert.Contains(pool, t => t is ExpertItem e && e.Name == "a1");
        Assert.Contains(pool, t => t is ExpertItem e && e.Name == "r1");
        Assert.Contains(pool, t => t is WorkflowItem w && w.Name == "w1");
    }

    // —— RestoreAsync / ResolveSaved ——

    [Fact]
    public async Task RestoreAsync_Reads_Saved_Selection_And_Expanded()
    {
        var id = Guid.NewGuid();
        var prefs = new FakePrefs
        {
            Values =
            {
                ["chatTargets"] = "{\"selected\":{\"kind\":\"Agent\",\"id\":\"" + id + "\"}}",
                ["recentExpanded"] = "0",
            }
        };
        var registry = NewRegistry(out _, out _, out _, prefs);

        var state = await registry.RestoreAsync();

        Assert.NotNull(state);
        Assert.Equal("Agent", state!.Selected!.Kind);
        Assert.Equal(id.ToString(), state.Selected.Id);
        Assert.True(state.RecentCollapsed); // recentExpanded == "0" → 收起
    }

    [Fact]
    public async Task RestoreAsync_Migrates_Legacy_SelectedAgentId()
    {
        var prefs = new FakePrefs { Values = { ["selectedAgentId"] = Guid.NewGuid().ToString() } };
        var registry = NewRegistry(out _, out _, out _, prefs);

        var state = await registry.RestoreAsync();

        Assert.NotNull(state);
        Assert.Equal("Agent", state!.Selected!.Kind);
        Assert.False(state.RecentCollapsed); // 无 recentExpanded 键 → 默认展开
    }

    [Fact]
    public async Task RestoreAsync_Returns_Null_When_Prefs_Unavailable()
    {
        var registry = NewRegistry(out _, out _, out _, new ThrowingPrefs());

        var state = await registry.RestoreAsync();

        Assert.Null(state); // 偏好读取失败 → VM 跳过整个恢复块（原 catch 语义）
    }

    private sealed class ThrowingPrefs : IPreferencesStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("prefs unavailable");
        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void ResolveSaved_Finds_Instance_By_Kind_And_Id()
    {
        var registry = NewRegistry(out var experts, out var workflows, out var rules);
        var agentId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        experts.Experts.Add(new ExpertItem { Id = agentId, Name = "a" });
        rules.Rules.Add(new ExpertItem { Id = ruleId, Name = "r", NodeType = NoteNodeType.Rule });
        workflows.Workflows.Add(new WorkflowItem { Id = wfId, Name = "w" });

        var agent = registry.ResolveSaved(new ChatTargetSelected { Kind = "Agent", Id = agentId.ToString() });
        var rule = registry.ResolveSaved(new ChatTargetSelected { Kind = "Rule", Id = ruleId.ToString() });
        var wf = registry.ResolveSaved(new ChatTargetSelected { Kind = "Workflow", Id = wfId.ToString() });
        var missing = registry.ResolveSaved(new ChatTargetSelected { Kind = "Agent", Id = Guid.NewGuid().ToString() });
        var badGuid = registry.ResolveSaved(new ChatTargetSelected { Kind = "Agent", Id = "not-a-guid" });

        Assert.Same(experts.Experts[0], agent);
        Assert.Same(rules.Rules[0], rule);
        Assert.Same(workflows.Workflows[0], wf);
        Assert.Null(missing);
        Assert.Null(badGuid);
    }

    // —— PersistSelectionAsync / CurrentSelectedPrefs ——

    [Fact]
    public async Task PersistSelection_RoundTrips_Through_Restore()
    {
        var prefs = new FakePrefs();
        var registry = NewRegistry(out var experts, out _, out _, prefs);
        var id = Guid.NewGuid();
        var agent = new ExpertItem { Id = id, Name = "a", NodeType = NoteNodeType.Agent };

        await registry.PersistSelectionAsync(agent);
        var state = await registry.RestoreAsync();

        Assert.NotNull(state);
        Assert.Equal("Agent", state!.Selected!.Kind);
        Assert.Equal(id.ToString(), state.Selected.Id);
    }

    [Fact]
    public async Task PersistSelection_Of_None_Clears_Saved()
    {
        var prefs = new FakePrefs { Values = { ["chatTargets"] = "{\"selected\":{\"kind\":\"Agent\",\"id\":\"x\"}}" } };
        var registry = NewRegistry(out _, out _, out _, prefs);

        await registry.PersistSelectionAsync(null);

        // None → Selected 序列化为 null（清空保存），Restore 出 Selected == null
        var state = await registry.RestoreAsync();
        Assert.NotNull(state);
        Assert.Null(state!.Selected);
    }

    [Fact]
    public void CurrentSelectedPrefs_Maps_Target_Shape()
    {
        var registry = NewRegistry(out _, out _, out _);
        var agentId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        var agentPrefs = registry.CurrentSelectedPrefs(new ExpertItem { Id = agentId, NodeType = NoteNodeType.Agent });
        var wfPrefs = registry.CurrentSelectedPrefs(new WorkflowItem { Id = wfId });
        var nonePrefs = registry.CurrentSelectedPrefs(null);

        Assert.Equal(("Agent", agentId.ToString()), (agentPrefs!.Kind, agentPrefs.Id));
        Assert.Equal(("Workflow", wfId.ToString()), (wfPrefs!.Kind, wfPrefs.Id));
        Assert.Null(nonePrefs);
    }
}
