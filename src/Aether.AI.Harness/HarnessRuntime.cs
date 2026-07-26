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
    /// returned once complete.
    /// </summary>
    public async Task<string> RunStreamingAsync(
        Guid sessionId,
        string userMessage,
        Func<string, CancellationToken, Task>? callback = null,
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

        var sb = new System.Text.StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(userMessage, frameworkSession, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                if (callback is not null)
                    await callback(update.Text, cancellationToken);
            }
        }

        var fullText = sb.ToString();
        session.Messages.Add(Message.Assistant(fullText));
        session.State = SessionState.Idle;
        session.Touch();
        return fullText;
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
