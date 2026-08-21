using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Workflows;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Workflows;
using Acme.Notes.Domain.Agents;
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
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("消息不能为空", nameof(message));

        var (agent, session) = await GetOrCreateAsync(conversationId, workflowId, cancellationToken).ConfigureAwait(false);
        await _runtime.RunStreamingAsync(agent, session, message, handlers, approvalResponse, desiredMode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry)) return null;
        return await HarnessRuntime.TryGetModeAsync(entry.agent, entry.session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry)) return false;
        return await HarnessRuntime.TrySetModeAsync(entry.agent, entry.session, mode, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(Guid workflowId)
    {
        foreach (var kv in _sessions.Where(x => x.Value.workflowId == workflowId).ToList())
            _sessions.TryRemove(kv.Key, out _);
    }

    public void EvictConversation(Guid conversationId)
    {
        // 会话删除：工作流 session 自管于 _sessions（不经 HarnessRuntime._frameworkSessions），
        // TryRemove 即切断其唯一强引用 → RestoringChatHistoryProvider 弱键缓存随 GC 自动回收。
        _sessions.TryRemove(conversationId, out _);
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
        // 注册工作流会话→conversationId，供 participant 的 RestoringChatHistoryProvider 重启后续聊恢复 LLM 历史
        // （工作流路径不经 HarnessRuntime.GetOrCreateFrameworkSessionAsync，故需显式注册）。
        _runtime.RegisterWorkflowSession(session, conversationId);
        _sessions[conversationId] = (workflowId, workflowAgent, session);
        return (workflowAgent, session);
    }
}
