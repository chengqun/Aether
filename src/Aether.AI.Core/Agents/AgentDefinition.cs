namespace Aether.AI.Core.Agents;

/// <summary>
/// Declarative description of an agent: its identity, instructions, the model
/// it should run on, and the names of tools it is allowed to use. This is a
/// plain data model — the Harness layer is responsible for turning an
/// <see cref="AgentDefinition"/> into a live, runnable agent.
/// </summary>
public sealed class AgentDefinition
{
    /// <summary>Stable logical identifier for the agent (e.g. "sales-assistant").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name shown to users.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The system prompt / instructions that shape agent behavior.</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>The model id to use (e.g. "gpt-4o-mini"). Resolved by the Harness.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Optional temperature override. <c>null</c> means use the provider default.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Names of tools (registered with the ToolRegistry) the agent may invoke.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Conversation memory policy. Defaults to <see cref="AgentMemoryMode.Full"/>.
    /// Enforced by the Harness via the framework's chat-history provider.
    /// </summary>
    public AgentMemoryMode Memory { get; init; } = AgentMemoryMode.Full;

    /// <summary>
    /// For <see cref="AgentMemoryMode.SlidingWindow"/>: how many of the most
    /// recent messages to retain. Ignored by other modes.
    /// </summary>
    public int MaxHistory { get; init; } = 20;
}
