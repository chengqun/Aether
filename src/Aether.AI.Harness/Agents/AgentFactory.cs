using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Builds a runnable Microsoft Agent Framework <see cref="AIAgent"/> from an
/// <see cref="AgentDefinition"/>. By default the agent is a
/// <see cref="HarnessAgent"/> — the batteries-included agent from
/// <c>Microsoft.Agents.AI.Harness</c> — with the heavyweight default providers
/// (web search, todos, file memory, skills, OpenTelemetry) disabled so that
/// Phase 1 runs as a clean, dependency-light runtime.
/// <para>
/// Set <see cref="UseChatClientAgent"/> to use the lighter
/// <see cref="ChatClientAgent"/> instead, which skips the Harness pipeline
/// entirely.
/// </para>
/// </summary>
public sealed class AgentFactory
{
    private readonly Func<IChatClient> _chatClientFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// When <c>true</c> (default), builds a <see cref="HarnessAgent"/>. When
    /// <c>false</c>, builds a plain <see cref="ChatClientAgent"/>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <param name="chatClientFactory">
    /// A factory that returns the <see cref="IChatClient"/>. Resolved lazily so
    /// that constructing an <see cref="AgentFactory"/> (and thus the runtime)
    /// does not eagerly build the chat client — which may require an API key.
    /// </param>
    public AgentFactory(Func<IChatClient> chatClientFactory, ToolRegistry toolRegistry, ILoggerFactory? loggerFactory = null)
    {
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _loggerFactory = loggerFactory;
    }

    /// <summary>Builds a cached, runnable agent for the given definition.</summary>
    public AIAgent Create(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        // AIFunction derives from AITool, but List<AIFunction> is not assignable
        // to IList<AITool>; widen to IList<AITool> for the framework APIs.
        IList<AITool> tools = _toolRegistry.ResolveAll(definition.Tools).Cast<AITool>().ToList();

        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools)
            : BuildHarnessAgent(definition, tools);
    }

    private AIAgent BuildHarnessAgent(AgentDefinition definition, IList<AITool> tools)
    {
        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            // Disable the heavyweight default providers so Phase 1 is a clean
            // runtime with no disk/network/OTel dependencies. These can be
            // re-enabled per agent in a later phase. (Compaction is auto-
            // disabled when no token limits are set, so it needs no flag here.)
            DisableWebSearch = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
            ChatOptions = BuildChatOptions(definition, tools),
        };

        return _chatClientFactory().AsHarnessAgent(options, _loggerFactory);
    }

    private AIAgent BuildChatClientAgent(AgentDefinition definition, IList<AITool> tools)
    {
        return new ChatClientAgent(
            _chatClientFactory(),
            instructions: definition.Instructions,
            name: definition.Name,
            description: $"Aether.AI agent '{definition.Id}'.",
            tools: tools,
            loggerFactory: _loggerFactory);
    }

    private static ChatOptions BuildChatOptions(AgentDefinition definition, IList<AITool> tools)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = definition.Instructions,
            Tools = tools,
        };

        if (definition.Temperature is { } temperature)
        {
            chatOptions.Temperature = (float)temperature;
        }

        return chatOptions;
    }
}
