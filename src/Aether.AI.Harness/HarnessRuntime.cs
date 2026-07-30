using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Messages;
using Aether.AI.Core.Sessions;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Both the framework and Aether.AI.Core define a type named AgentSession.
// Alias them to avoid ambiguity: the framework's is the model-side chat
// thread; Aether's is the runtime execution context.
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;
using AetherSession = Aether.AI.Core.Sessions.AgentSession;

namespace Aether.AI.Harness;

/// <summary>
/// The Aether runtime: turns registered <see cref="AgentDefinition"/>s into
/// live agents, owns session lifecycle, and drives multi-turn execution on top
/// of Microsoft Agent Framework.
/// <para>
/// Responsibilities (Phase 1):
/// <list type="bullet">
///   <item>Agent creation (delegated to <see cref="AgentFactory"/>, cached).</item>
///   <item>Session management (delegated to <see cref="SessionManager"/>).</item>
///   <item>Multi-turn conversation via the framework's <c>AgentSession</c> thread.</item>
///   <item>Recording messages and tool calls into the Aether <see cref="AgentSession"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HarnessRuntime
{
    private readonly AgentFactory _factory;
    private readonly SessionManager _sessions;
    private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();
    private readonly ConcurrentDictionary<Guid, FrameworkAgentSession> _frameworkSessions = new();

    public HarnessRuntime(AgentFactory factory, SessionManager sessions)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public SessionManager Sessions => _sessions;

    /// <summary>Registers an agent definition so it can be started by id.</summary>
    public HarnessRuntime RegisterAgent(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        _definitions[definition.Id] = definition;
        return this;
    }

    /// <summary>True if an agent with the given id is registered.</summary>
    public bool HasAgent(string agentId) => _definitions.ContainsKey(agentId);

    /// <summary>
    /// 使已注册 agent 的缓存失效：移除其定义与已构建的 agent 实例，下次运行时重建。
    /// 用于自定义 Agent 被编辑后丢弃旧配置。会话线程由调用方（runner）丢弃并新建。
    /// </summary>
    public HarnessRuntime Invalidate(string agentId)
    {
        _definitions.TryRemove(agentId, out _);
        _agents.TryRemove(agentId, out _);
        return this;
    }

    /// <summary>Starts a new runtime session for the named agent.</summary>
    public AetherSession StartSession(string agentId)
    {
        if (!_definitions.TryGetValue(agentId, out var definition))
            throw new InvalidOperationException($"No agent registered with id '{agentId}'.");

        return _sessions.Create(definition.Id, definition.Name);
    }

    /// <summary>
    /// Runs one turn of the agent bound to <paramref name="sessionId"/>,
    /// sending <paramref name="userMessage"/> and returning the assistant's
    /// textual response. Prior turns in the same session are remembered.
    /// </summary>
    public async Task<string> RunAsync(
        Guid sessionId,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = ResolveDefinition(session.AgentId);
        var agent = GetOrCreateAgent(definition);
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        session.Messages.Add(Message.User(userMessage));
        session.State = SessionState.Active;
        session.Touch();

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(userMessage, frameworkSession, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            session.State = SessionState.Faulted;
            session.Properties["lastError"] = ex.Message;
            session.Touch();
            throw;
        }

        var assistantText = response.Text ?? string.Empty;
        session.Messages.Add(Message.Assistant(assistantText));

        // Record any tool invocations the framework performed this turn.
        foreach (var toolName in ExtractToolCalls(response))
        {
            session.ToolCalls.Add(toolName);
        }

        session.State = SessionState.Idle;
        session.Touch();
        return assistantText;
    }

    /// <summary>
    /// Runs one turn as a streaming response. The assistant's text is streamed
    /// to <paramref name="callback"/> as it arrives and the full text is
    /// returned once complete. Tool invocations/results encountered in the
    /// stream are surfaced via <paramref name="onToolEvent"/> (if provided).
    /// </summary>
    public async Task<string> RunStreamingAsync(
        Guid sessionId,
        string userMessage,
        Func<string, CancellationToken, Task>? callback = null,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent = null,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = ResolveDefinition(session.AgentId);
        var agent = GetOrCreateAgent(definition);
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        session.Messages.Add(Message.User(userMessage));
        session.State = SessionState.Active;
        session.Touch();

        var fullText = await RunStreamingCoreAsync(agent, frameworkSession, userMessage, callback, onToolEvent, cancellationToken,
            name => session.ToolCalls.Add(name));

        session.Messages.Add(Message.Assistant(fullText));
        session.State = SessionState.Idle;
        session.Touch();
        return fullText;
    }

    /// <summary>
    /// 用一个**已构建好的** <see cref="AIAgent"/>（如 workflow 包成的 agent）流式运行一轮，
    /// 复用与 <see cref="RunStreamingAsync(Guid,string,Func{string,CancellationToken,Task}?,Func{ToolStreamEvent,CancellationToken,Task}?,CancellationToken)"/>
    /// 相同的流式循环 + ToolStreamEvent 提取。无 AetherSession 簿记（调用方自管会话）。
    /// 供 ICustomWorkflowRunner 等把组合 agent 当单 agent 跑的场景使用。
    /// </summary>
    public async Task<string> RunStreamingAsync(
        AIAgent agent,
        FrameworkAgentSession frameworkSession,
        string userMessage,
        Func<string, CancellationToken, Task>? callback = null,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent = null,
        CancellationToken cancellationToken = default)
        => await RunStreamingCoreAsync(agent, frameworkSession, userMessage, callback, onToolEvent, cancellationToken, onToolCallName: null);

    /// <summary>
    /// 流式运行核心：遍历 <see cref="AIAgent.RunStreamingAsync"/>，上抛文本 token 与工具调用/结果事件。
    /// <paramref name="onToolCallName"/> 供带 AetherSession 的调用方记录工具名（可选）。
    /// </summary>
    private static async Task<string> RunStreamingCoreAsync(
        AIAgent agent, FrameworkAgentSession frameworkSession, string userMessage,
        Func<string, CancellationToken, Task>? callback,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent,
        CancellationToken cancellationToken,
        Action<string>? onToolCallName)
    {
        var sb = new System.Text.StringBuilder();
        // callId -> 工具名：FunctionResultContent 无 Name 字段，靠 CallId 回查调用时记录的名称。
        var toolNamesByCallId = new Dictionary<string, string>();
        await foreach (var update in agent.RunStreamingAsync(userMessage, frameworkSession, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                if (callback is not null)
                    await callback(update.Text, cancellationToken).ConfigureAwait(false);
            }

            // 把流中的工具调用/结果内容片段上抛给 UI（思考过程展示）。
            if (onToolEvent is not null && update.Contents is not null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.Name))
                    {
                        if (!string.IsNullOrEmpty(call.CallId))
                            toolNamesByCallId[call.CallId] = call.Name!;
                        onToolCallName?.Invoke(call.Name!);
                        await onToolEvent(
                            new ToolStreamEvent(ToolStreamEventKind.Call, call.Name!, call.CallId ?? string.Empty,
                                ToInputDict(call.Arguments), null),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (content is FunctionResultContent result)
                    {
                        var name = (result.CallId is not null && toolNamesByCallId.TryGetValue(result.CallId, out var n))
                            ? n : "工具";
                        await onToolEvent(
                            new ToolStreamEvent(ToolStreamEventKind.Result, name, result.CallId ?? string.Empty,
                                null, ToOutputDict(result.Result)),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>把 FunctionCallContent.Arguments（可能为字典/JsonElement/字符串）归一为可展示字典。</summary>
    private static Dictionary<string, object>? ToInputDict(object? arguments)
    {
        if (arguments is null) return null;

        if (arguments is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (arguments is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);
        if (arguments is IReadOnlyDictionary<string, object?> rnd)
            return rnd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);

        if (arguments is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["input"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["input"] = arguments.ToString() ?? string.Empty };
    }

    /// <summary>把 FunctionResultContent.Result 归一为可展示字典。</summary>
    private static Dictionary<string, object>? ToOutputDict(object? result)
    {
        if (result is null) return null;

        if (result is string s)
            return new Dictionary<string, object> { ["result"] = s };

        if (result is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (result is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);

        if (result is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["result"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["result"] = result.ToString() ?? string.Empty };
    }

    private AgentDefinition ResolveDefinition(string agentId)
        => _definitions.TryGetValue(agentId, out var definition)
            ? definition
            : throw new InvalidOperationException($"No agent registered with id '{agentId}'.");

    private AIAgent GetOrCreateAgent(AgentDefinition definition)
        => _agents.GetOrAdd(definition.Id, _ => _factory.Create(definition));

    private async Task<FrameworkAgentSession> GetOrCreateFrameworkSessionAsync(
        Guid sessionId, AIAgent agent, CancellationToken cancellationToken)
    {
        if (_frameworkSessions.TryGetValue(sessionId, out var existing))
            return existing;

        var created = await agent.CreateSessionAsync(cancellationToken);
        _frameworkSessions[sessionId] = created;
        return created;
    }

    private static IEnumerable<string> ExtractToolCalls(AgentResponse response)
    {
        var names = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.Name))
                    names.Add(call.Name);
            }
        }
        return names;
    }
}
