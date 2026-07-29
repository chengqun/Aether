using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Acme.Notes.AgentSource;
using NotesAgentMemoryMode = Acme.Notes.AgentSource.AgentMemoryMode;

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
        Func<string, CancellationToken, Task>? onToken = null,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("消息不能为空", nameof(message));

        var id = agentId.ToString();

        // 按需注册（首次或 Invalidate 后）
        if (!_runtime.HasAgent(id))
        {
            var def = await _catalog.GetAsync(agentId, cancellationToken)
                ?? throw new InvalidOperationException($"找不到自定义 Agent：{agentId}");
            _runtime.RegisterAgent(ToAgentDefinition(id, def));
        }

        var sessionId = _sessionsByConversation.GetOrAdd(
            conversationId,
            _ => (_runtime.StartSession(id).SessionId, agentId)).sessionId;

        await _runtime.RunStreamingAsync(sessionId, message, onToken, onToolEvent, cancellationToken);
    }

    public void Invalidate(Guid agentId)
    {
        // 清掉该 agent 的所有会话（编辑配置后丢弃旧记忆）
        foreach (var kv in _sessionsByConversation.Where(x => x.Value.agentId == agentId).ToList())
            _sessionsByConversation.TryRemove(kv.Key, out _);
        _runtime.Invalidate(agentId.ToString());
    }

    internal static AgentDefinition ToAgentDefinition(string id, CustomAgentDefinition def)
        => new()
        {
            Id = id,
            Name = def.Name,
            Instructions = def.SystemPrompt,
            Tools = def.Tools,
            Skills = def.Skills,
            ModelId = def.ModelId ?? string.Empty,
            Memory = MapMemory(def.Memory),
            MaxHistory = def.MaxHistory,
            TopP = def.TopP,
            MaxOutputTokens = def.MaxOutputTokens,
            ResponseFormat = MapResponseFormat(def.ResponseFormat),
            EnableWebSearch = def.EnableWebSearch,
            EnableTodoProvider = def.EnableTodoProvider,
            EnableAgentModeProvider = def.EnableAgentModeProvider,
            EnableFileMemory = def.EnableFileMemory,
            EnableAgentSkills = def.EnableAgentSkills,
            EnableOpenTelemetry = def.EnableOpenTelemetry,
        };

    /// <summary>Notes 层 AgentResponseFormat → 平台 Core AgentResponseFormat（同名不同程序集，显式映射）。</summary>
    private static Aether.AI.Core.Agents.AgentResponseFormat MapResponseFormat(Acme.Notes.AgentSource.AgentResponseFormat format)
        => format switch
        {
            Acme.Notes.AgentSource.AgentResponseFormat.Text => Aether.AI.Core.Agents.AgentResponseFormat.Text,
            Acme.Notes.AgentSource.AgentResponseFormat.Json => Aether.AI.Core.Agents.AgentResponseFormat.Json,
            _ => Aether.AI.Core.Agents.AgentResponseFormat.Default,
        };

    /// <summary>Notes 的 <see cref="NotesAgentMemoryMode"/> 与平台 <see cref="Aether.AI.Core.Agents.AgentMemoryMode"/> 同名同序，直接映射。</summary>
    private static Aether.AI.Core.Agents.AgentMemoryMode MapMemory(NotesAgentMemoryMode memory)
        => memory switch
        {
            NotesAgentMemoryMode.SlidingWindow => Aether.AI.Core.Agents.AgentMemoryMode.SlidingWindow,
            NotesAgentMemoryMode.Off => Aether.AI.Core.Agents.AgentMemoryMode.Off,
            _ => Aether.AI.Core.Agents.AgentMemoryMode.Full,
        };
}
