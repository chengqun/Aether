using Acme.Desktop.Mvvm;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// 聊天下拉池自愈刷新契约单测：三个宿主 VM（Experts/Workflows/Rules）的 <c>LoadAsync</c> 成功填充列表后
/// **必须发射 <c>TargetsChanged</c>**——ChatWorkspaceViewModel 订阅它重建统一下拉池。这是「首轮启动下拉缺
/// agent，重启才有」的修复锁：无论列表加载晚于 Chat 初始化多久（慢加载/时序竞态/登录后二次 init），
/// 池都会在列表就绪后自愈刷新（RefreshChatTargetItems 按 (Id,Kind) 静默保留选中，不清空会话）。
/// </summary>
public class ChatTargetPoolRefreshTests
{
    // —— stub 依赖（镜像 ChatTargetRegistryTests，可独立构造三个宿主 VM） ——

    private sealed class FakeDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    /// <summary>可控返回的 agent 目录：按调用序依次返回预设列表（模拟「初始空、后补数据」的延迟加载）。</summary>
    private sealed class ScriptedAgentCatalog : ICustomAgentCatalog
    {
        private readonly Queue<IReadOnlyList<CustomAgentDefinition>> _script;
        public int ListCalls;

        public ScriptedAgentCatalog(params IReadOnlyList<CustomAgentDefinition>[] rounds) => _script = new(rounds);

        public Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : Array.Empty<CustomAgentDefinition>());
        }
        public Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<CustomAgentDefinition?>(null);
        public Task<CustomAgentDefinition?> GetByMarketplaceIdAsync(Guid marketplaceId, CancellationToken ct = default) => Task.FromResult<CustomAgentDefinition?>(null);
        public Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<CustomToolMeta> GetAvailableTools() => Array.Empty<CustomToolMeta>();
        public string CurrentHost => "test";
        public IReadOnlyList<CustomModelOption> GetAvailableModels() => Array.Empty<CustomModelOption>();
    }

    private sealed class ThrowingAgentCatalog : ICustomAgentCatalog
    {
        public Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
            => throw new IOException("disk");
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
        private readonly Queue<IReadOnlyList<CustomWorkflowDefinition>> _script;

        public StubWorkflowCatalog(params IReadOnlyList<CustomWorkflowDefinition>[] rounds) => _script = new(rounds);

        public Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(_script.Count > 0 ? _script.Dequeue() : Array.Empty<CustomWorkflowDefinition>());
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
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static CustomAgentDefinition AgentDef(Guid id, string name, NoteNodeType type = NoteNodeType.Agent) => new()
    {
        Id = id,
        Name = name,
        Category = "普通对话",
        SystemPrompt = "p",
        NodeType = type,
        Engine = type == NoteNodeType.Rule ? "Rule" : "Llm",
    };

    // —— ExpertsViewModel：LoadAsync 成功 → TargetsChanged ——

    [Fact]
    public async Task Experts_LoadAsync_Fires_TargetsChanged_When_List_Populates()
    {
        var id = Guid.NewGuid();
        var experts = new ExpertsViewModel(new ScriptedAgentCatalog(new[] { AgentDef(id, "a1") }),
            new StubAgentRunner(), new FakeDispatcher(), new McpViewModel(new StubMcpManagement(), new FakeDispatcher()));
        int fired = 0;
        experts.TargetsChanged += () => fired++;

        await experts.LoadAsync();

        Assert.Equal(1, fired);
        Assert.Single(experts.Experts);
        Assert.Equal(id, experts.Experts[0].Id);
    }

    [Fact]
    public async Task Experts_LoadAsync_Late_Reload_Fires_Again_With_New_Item()
    {
        // 延迟加载场景：首轮空列表（Chat 池快照为空）→ 数据后到 → 再 LoadAsync 必须再次通知，池才能自愈。
        var catalog = new ScriptedAgentCatalog(Array.Empty<CustomAgentDefinition>(), new[] { AgentDef(Guid.NewGuid(), "late") });
        var experts = new ExpertsViewModel(catalog, new StubAgentRunner(),
            new FakeDispatcher(), new McpViewModel(new StubMcpManagement(), new FakeDispatcher()));
        int fired = 0;
        experts.TargetsChanged += () => fired++;

        await experts.LoadAsync(); // 首轮：空
        await experts.LoadAsync(); // 次轮：agent 到场

        Assert.Equal(2, fired);
        Assert.Single(experts.Experts);
    }

    [Fact]
    public async Task Experts_LoadAsync_Failure_Does_Not_Fire()
    {
        // 加载失败走 catch（列表未变）→ 不发通知（池保持原状，不误触发重建）。
        var experts = new ExpertsViewModel(new ThrowingAgentCatalog(), new StubAgentRunner(),
            new FakeDispatcher(), new McpViewModel(new StubMcpManagement(), new FakeDispatcher()));
        int fired = 0;
        experts.TargetsChanged += () => fired++;

        await experts.LoadAsync();

        Assert.Equal(0, fired);
        Assert.Empty(experts.Experts);
    }

    // —— RulesViewModel：同契约 ——

    [Fact]
    public async Task Rules_LoadAsync_Fires_TargetsChanged_When_List_Populates()
    {
        var rules = new RulesViewModel(new ScriptedAgentCatalog(new[] { AgentDef(Guid.NewGuid(), "r1", NoteNodeType.Rule) }),
            new StubAgentRunner(), new FakeDispatcher(), new McpViewModel(new StubMcpManagement(), new FakeDispatcher()));
        int fired = 0;
        rules.TargetsChanged += () => fired++;

        await rules.LoadAsync();

        Assert.Equal(1, fired);
        Assert.Single(rules.Rules);
    }

    // —— WorkflowsViewModel：同契约 ——

    [Fact]
    public async Task Workflows_LoadAsync_Fires_TargetsChanged_When_List_Populates()
    {
        var wf = new WorkflowItem();
        var workflows = new WorkflowsViewModel(
            new StubWorkflowCatalog(new[] { new CustomWorkflowDefinition
            {
                Id = wf.Id, Name = "w1", Description = "d", EntryAgentId = Guid.NewGuid(),
                Participants = new List<Guid> { Guid.NewGuid() },
            } }),
            new ScriptedAgentCatalog(), new StubWorkflowRunner(), new FakeDispatcher());
        int fired = 0;
        workflows.TargetsChanged += () => fired++;

        await workflows.LoadAsync();

        Assert.Equal(1, fired);
        Assert.Single(workflows.Workflows);
    }

    // —— 端到端：池自愈（列表晚于池快照到场 → 通知触发重建后池含新目标） ——

    [Fact]
    public async Task Late_Agent_Arrival_Heals_Pool_Via_TargetsChanged()
    {
        // 复刻启动竞态：池先按空列表快照构建（Chat 初始化早于列表就绪），随后 LoadAsync 完成 →
        // TargetsChanged → 消费方重建池（此处用 ChatTargetRegistry.BuildPool 模拟 RefreshChatTargetItems 的重建读）。
        var catalog = new ScriptedAgentCatalog(Array.Empty<CustomAgentDefinition>(), new[] { AgentDef(Guid.NewGuid(), "late") });
        var disp = new FakeDispatcher();
        var experts = new ExpertsViewModel(catalog, new StubAgentRunner(), disp,
            new McpViewModel(new StubMcpManagement(), disp));
        var rules = new RulesViewModel(new ScriptedAgentCatalog(), new StubAgentRunner(), disp,
            new McpViewModel(new StubMcpManagement(), disp));
        var workflows = new WorkflowsViewModel(new StubWorkflowCatalog(), new ScriptedAgentCatalog(),
            new StubWorkflowRunner(), disp);
        var registry = new ChatTargetRegistry(experts, workflows, rules, new FakePrefs());

        await experts.LoadAsync(); // 首轮：空
        var poolSnapshot = registry.BuildPool(); // 模拟 Chat 初始化时的池快照
        Assert.Empty(poolSnapshot);

        await experts.LoadAsync(); // 次轮：agent 到场（生产代码里 TargetsChanged → RefreshChatTargetItems 重建）

        var healed = registry.BuildPool();
        Assert.Single(healed); // 重建后池含该 agent——下拉框不再缺失
    }
}
