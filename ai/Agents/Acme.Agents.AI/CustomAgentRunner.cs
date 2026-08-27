using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using NotesAgentMemoryMode = Acme.Notes.Domain.Agents.AgentMemoryMode;
using NotesAgentResponseFormat = Acme.Notes.Domain.Agents.AgentResponseFormat;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomAgentRunner"/> 默认实现：桥接 <see cref="ICustomAgentCatalog"/>
/// 与平台 <see cref="HarnessRuntime"/>。按 agentId 缓存会话以支持多轮；编辑后调
/// <see cref="Invalidate"/> 重建。
/// </summary>
public sealed class CustomAgentRunner : ICustomAgentRunner
{
    private readonly HarnessRuntime _runtime;
    private readonly ICustomAgentCatalog _catalog;
    // conversationId -> (runtime sessionId, agentId)：每会话独立运行时会话，多会话记忆隔离。
    private readonly ConcurrentDictionary<Guid, (Guid sessionId, Guid agentId)> _sessionsByConversation = new();

    public CustomAgentRunner(HarnessRuntime runtime, ICustomAgentCatalog catalog)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task RunStreamingAsync(
        Guid conversationId,
        Guid agentId,
        string message,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("消息不能为空", nameof(message));

        var id = agentId.ToString();

        // 按需注册（首次或 Invalidate 后）。诊断日志：编辑 agent 后 Invalidate 会让 HasAgent=false → 走此分支重新 GetAsync（新 tools），
        // 而 sessionId 复用（映射未删）→ 历史由 RestoringChatHistoryProvider 从 DB 恢复。三信号可验证「新 tools 生效 + 不丢记忆」。
        if (!_runtime.HasAgent(id))
        {
            System.Diagnostics.Trace.WriteLine($"[AgentRebuild] re-register agent {id} (HasAgent=false → GetAsync 新定义)");
            var def = await _catalog.GetAsync(agentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"找不到自定义 Agent：{agentId}");
            _runtime.RegisterAgent(ToAgentDefinition(id, def));
        }

        var sessionId = _sessionsByConversation.GetOrAdd(
            conversationId,
            _ => (_runtime.StartSession(id).SessionId, agentId)).sessionId;

        await _runtime.RunStreamingAsync(sessionId, message, handlers, approvalResponse, desiredMode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!_sessionsByConversation.TryGetValue(conversationId, out var entry)) return null;
        return await _runtime.GetModeAsync(entry.sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default)
    {
        if (!_sessionsByConversation.TryGetValue(conversationId, out var entry)) return false;
        return await _runtime.SetModeAsync(entry.sessionId, mode, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(Guid agentId)
    {
        // 编辑配置后让新定义（tools/systemPrompt/model 等）下次对话生效，但**不重置会话、不丢多轮记忆**：
        // ① _runtime.Invalidate 清 definition + agent 实例缓存 → 下次 run HasAgent=false → 重新 GetAsync + RegisterAgent（新定义）。
        // ② 仅清各会话的 frameworkSession（绑旧 agent 实例/旧 tools），**保留** _sessionsByConversation 的
        //    conversationId→sessionId 映射 + SessionManager 里的 AetherSession → 下次 run 复用同一 sessionId，
        //    frameworkSession 重建后由 RestoringChatHistoryProvider 按 conversationId 从 DB 恢复历史。
        //    旧实现误删映射 → 下次 run StartSession 开全新 session，丢记忆 + 泄漏旧 session。
        foreach (var kv in _sessionsByConversation.Where(x => x.Value.agentId == agentId))
            _runtime.RebuildSessionFramework(kv.Value.sessionId);
        _runtime.Invalidate(agentId.ToString());
    }

    public void EvictConversation(Guid conversationId)
    {
        // 会话删除：丢弃 conversationId→sessionId 映射并清平台运行时会话。
        // agent 路径的 FrameworkAgentSession 存于 HarnessRuntime._frameworkSessions + SessionManager，
        // EvictSession 切断这两个强引用 → RestoringChatHistoryProvider 的弱键缓存随 GC 自动回收。
        if (_sessionsByConversation.TryRemove(conversationId, out var entry))
            _runtime.EvictSession(entry.sessionId);
    }

    internal static AgentDefinition ToAgentDefinition(string id, CustomAgentDefinition def)
    {
        // 规则 agent：body(SystemPrompt) 是 YAML 规则程序，由适配器解析成 RuleProgram 对象直接喂平台 Rules seam；
        // 规则不消费 system prompt（Instructions 置空）。代码规则 agent（NodeType==CodeRule）：body 是 C# 源码，
        // 不解析成 RuleProgram——IsCodeRule=true 让 AgentFactory 从 ICodeBrainRegistry 取代码大脑。LLM agent：body 是 system prompt。
        var isRule = Enum.TryParse<EngineKind>(def.Engine, true, out var engine) && engine == EngineKind.Rule;
        var isCodeRule = isRule && def.NodeType == NoteNodeType.CodeRule;
        RuleProgram? rules = isRule && !isCodeRule && RuleProgramYaml.TryDeserialize(def.SystemPrompt, out var p) ? p : null;
        return new AgentDefinition
        {
            Id = id,
            Name = def.Name,
            Instructions = isRule ? string.Empty : def.SystemPrompt,
            Rules = rules,
            Engine = engine,
            IsCodeRule = isCodeRule,
            Tools = def.Tools,
            ToolApprovals = def.ToolApprovals,
            AutoApprovalRules = def.AutoApprovalRules,
            ModelId = def.ModelId ?? string.Empty,
            Memory = MapMemory(def.Memory),
            MaxHistory = def.MaxHistory,
            TopP = def.TopP,
            MaxOutputTokens = def.MaxOutputTokens,
            ResponseFormat = MapResponseFormat(def.ResponseFormat),
            EnableWebSearch = def.EnableWebSearch,
            EnableTodoProvider = def.EnableTodoProvider,
            RequireToolApproval = def.RequireToolApproval,
            EnableAgentModeProvider = def.EnableAgentModeProvider,
            EnableFileMemory = def.EnableFileMemory,
            EnableOpenTelemetry = def.EnableOpenTelemetry,
            EnableLongTermMemory = def.EnableLongTermMemory,
        };
    }

    /// <summary>Notes 层 AgentResponseFormat → 平台 Core AgentResponseFormat（同名不同程序集，显式映射）。</summary>
    private static Aether.AI.Core.Agents.AgentResponseFormat MapResponseFormat(NotesAgentResponseFormat format)
        => format switch
        {
            NotesAgentResponseFormat.Text => Aether.AI.Core.Agents.AgentResponseFormat.Text,
            NotesAgentResponseFormat.Json => Aether.AI.Core.Agents.AgentResponseFormat.Json,
            _ => Aether.AI.Core.Agents.AgentResponseFormat.Default,
        };

    /// <summary>Notes 的 <see cref="NotesAgentMemoryMode"/> 与平台 <see cref="Aether.AI.Core.Agents.AgentMemoryMode"/> 同名同序，直接映射。</summary>
    private static Aether.AI.Core.Agents.AgentMemoryMode MapMemory(NotesAgentMemoryMode memory)
        => memory switch
        {
            NotesAgentMemoryMode.SlidingWindow => Aether.AI.Core.Agents.AgentMemoryMode.SlidingWindow,
            NotesAgentMemoryMode.Off => Aether.AI.Core.Agents.AgentMemoryMode.Off,
            NotesAgentMemoryMode.Summarize => Aether.AI.Core.Agents.AgentMemoryMode.Summarize,
            _ => Aether.AI.Core.Agents.AgentMemoryMode.Full,
        };
}
