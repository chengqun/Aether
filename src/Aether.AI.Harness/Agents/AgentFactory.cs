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
/// <para>
/// The agent's <see cref="AgentDefinition.ModelId"/> is resolved to an
/// <see cref="IChatClient"/> via <see cref="IChatClientFactory"/>, and its
/// <see cref="AgentDefinition.Memory"/> policy is enforced by configuring the
/// framework's chat-history provider at build time.
/// </para>
/// </summary>
public sealed class AgentFactory
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// When <c>true</c> (default), builds a <see cref="HarnessAgent"/>. When
    /// <c>false</c>, builds a plain <see cref="ChatClientAgent"/>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <param name="chatClientFactory">
    /// Resolves the <see cref="IChatClient"/> for a given agent model id.
    /// Invoked lazily per agent so the runtime does not eagerly build a client.
    /// </param>
    public AgentFactory(IChatClientFactory chatClientFactory, ToolRegistry toolRegistry, ILoggerFactory? loggerFactory = null)
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
        var historyProvider = BuildHistoryProvider(definition);

        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools, historyProvider)
            : BuildHarnessAgent(definition, tools, historyProvider);
    }

    private AIAgent BuildHarnessAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            // Provider 开关由 AgentDefinition 的 Enable* 字段驱动（默认全 false = 全部禁用），
            // 保持 Phase 1 的轻量运行时行为；需要 web search / file memory / OTel 等能力的
            // agent 在定义里把对应 Enable* 置 true 即可。(Compaction 在未设 token 上限时自动禁用，无需开关。)
            DisableWebSearch = !definition.EnableWebSearch,
            DisableTodoProvider = !definition.EnableTodoProvider,
            DisableAgentModeProvider = !definition.EnableAgentModeProvider,
            DisableFileMemory = !definition.EnableFileMemory,
            DisableAgentSkillsProvider = !definition.EnableAgentSkills,
            DisableOpenTelemetry = !definition.EnableOpenTelemetry,
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
        };

        return _chatClientFactory.Create(definition.ModelId).AsHarnessAgent(options, _loggerFactory);
    }

    private AIAgent BuildChatClientAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        var client = _chatClientFactory.Create(definition.ModelId);

        // When no custom history provider is needed (Full), use the lightweight
        // ctor which defaults to InMemoryChatHistoryProvider. Otherwise wire the
        // provider through ChatClientAgentOptions.
        if (historyProvider is null)
        {
            return new ChatClientAgent(
                client,
                instructions: definition.Instructions,
                name: definition.Name,
                description: $"Aether.AI agent '{definition.Id}'.",
                tools: tools,
                loggerFactory: _loggerFactory);
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
        };
        return new ChatClientAgent(client, agentOptions, _loggerFactory);
    }

    /// <summary>
    /// Builds the framework chat-history provider that enforces the agent's
    /// memory policy. <c>null</c> (Full) lets the framework use its default
    /// unbounded in-memory provider.
    /// </summary>
    private static ChatHistoryProvider? BuildHistoryProvider(AgentDefinition definition)
        => definition.Memory switch
        {
            AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
            AgentMemoryMode.SlidingWindow => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            _ => null, // Full = framework default
        };

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

        if (definition.TopP is { } topP)
        {
            chatOptions.TopP = (float)topP;
        }

        if (definition.MaxOutputTokens is { } maxTokens)
        {
            chatOptions.MaxOutputTokens = maxTokens;
        }

        if (definition.ResponseFormat != AgentResponseFormat.Default)
        {
            chatOptions.ResponseFormat = definition.ResponseFormat switch
            {
                AgentResponseFormat.Text => ChatResponseFormat.Text,
                AgentResponseFormat.Json => ChatResponseFormat.Json,
                _ => null,
            };
        }

        return chatOptions;
    }
}
