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
    private readonly ConcurrentDictionary<Guid, Guid> _sessionsByAgent = new(); // agentId -> sessionId

    public CustomAgentRunner(HarnessRuntime runtime, ICustomAgentCatalog catalog)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task RunStreamingAsync(
        Guid agentId,
        string message,
        Func<string, CancellationToken, Task>? onToken = null,
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

        var sessionId = _sessionsByAgent.GetOrAdd(agentId, _ => _runtime.StartSession(id).SessionId);

        await _runtime.RunStreamingAsync(sessionId, message, onToken, cancellationToken);
    }

    public void Invalidate(Guid agentId)
    {
        _sessionsByAgent.TryRemove(agentId, out _);
        _runtime.Invalidate(agentId.ToString());
    }

    private static AgentDefinition ToAgentDefinition(string id, CustomAgentDefinition def)
        => new()
        {
            Id = id,
            Name = def.Name,
            Instructions = def.SystemPrompt,
            Tools = def.Tools,
            ModelId = def.ModelId ?? string.Empty,
            Memory = MapMemory(def.Memory),
            MaxHistory = def.MaxHistory,
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
