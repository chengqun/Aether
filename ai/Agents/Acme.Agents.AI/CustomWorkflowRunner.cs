using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Workflows;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Workflows;
using Microsoft.Agents.AI;

// MAF 的 AgentSession 与 Aether 的同名，别名区分（同 HarnessRuntime）。
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomWorkflowRunner"/> 默认实现：读 workflow def → 用 <see cref="AgentFactory"/> 建参与者 AIAgent →
/// <see cref="WorkflowFactory.BuildAsAIAgent"/> 包成 workflow AIAgent → 经 <see cref="HarnessRuntime"/> 流式跑。
/// 复用 ToolStreamEvent 提取与分段展示。每会话缓存 (workflowAIAgent + FrameworkAgentSession) 做多轮记忆。
/// </summary>
public sealed class CustomWorkflowRunner : ICustomWorkflowRunner
{
    private readonly HarnessRuntime _runtime;
    private readonly AgentFactory _agentFactory;
    private readonly ICustomAgentCatalog _agentCatalog;
    private readonly ICustomWorkflowCatalog _workflowCatalog;
    // conversationId -> (workflowId, workflowAIAgent, session)
    private readonly ConcurrentDictionary<Guid, (Guid workflowId, AIAgent agent, FrameworkAgentSession session)> _sessions = new();

    public CustomWorkflowRunner(HarnessRuntime runtime, AgentFactory agentFactory, ICustomAgentCatalog agentCatalog, ICustomWorkflowCatalog workflowCatalog)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _agentCatalog = agentCatalog ?? throw new ArgumentNullException(nameof(agentCatalog));
        _workflowCatalog = workflowCatalog ?? throw new ArgumentNullException(nameof(workflowCatalog));
    }

    public async Task RunStreamingAsync(
        Guid conversationId, Guid workflowId, string message,
        Func<string, CancellationToken, Task>? onToken = null,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("消息不能为空", nameof(message));

        var (agent, session) = await GetOrCreateAsync(conversationId, workflowId, cancellationToken).ConfigureAwait(false);
        await _runtime.RunStreamingAsync(agent, session, message, onToken, onToolEvent, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(Guid workflowId)
    {
        foreach (var kv in _sessions.Where(x => x.Value.workflowId == workflowId).ToList())
            _sessions.TryRemove(kv.Key, out _);
    }

    private async Task<(AIAgent agent, FrameworkAgentSession session)> GetOrCreateAsync(Guid conversationId, Guid workflowId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(conversationId, out var existing) && existing.workflowId == workflowId)
            return (existing.agent, existing.session);

        var def = await _workflowCatalog.GetAsync(workflowId, ct)
            ?? throw new InvalidOperationException($"找不到 workflow：{workflowId}");

        // 建参与者 AIAgent（每个 participant 是一个自定义 agent）
        var participants = new Dictionary<Guid, AIAgent>();
        foreach (var pid in def.Participants)
        {
            var agentDef = await _agentCatalog.GetAsync(pid, ct)
                ?? throw new InvalidOperationException($"workflow 参与者 agent 未找到：{pid}");
            participants[pid] = _agentFactory.Create(CustomAgentRunner.ToAgentDefinition(pid.ToString(), agentDef));
        }

        var workflowDef = new WorkflowDefinition
        {
            Id = workflowId.ToString(),
            Name = def.Name,
            Description = def.Description,
            Category = def.Category,
            EntryAgentId = def.EntryAgentId,
            Participants = def.Participants,
            Handoffs = def.Handoffs.Select(h => new WorkflowHandoff
            {
                FromAgentId = h.FromAgentId,
                ToAgentId = h.ToAgentId,
                Reason = h.Reason,
            }).ToList(),
            HandoffInstructions = def.HandoffInstructions,
        };

        var workflowAgent = WorkflowFactory.BuildAsAIAgent(workflowDef, participants, workflowId.ToString(), def.Name, def.Description);
        var session = await workflowAgent.CreateSessionAsync(ct);
        _sessions[conversationId] = (workflowId, workflowAgent, session);
        return (workflowAgent, session);
    }
}
