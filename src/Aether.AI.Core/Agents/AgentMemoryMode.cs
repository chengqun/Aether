namespace Aether.AI.Core.Agents;

/// <summary>
/// Agent conversation memory policy. Determines how prior turns are retained
/// across runs within the same session.
/// </summary>
public enum AgentMemoryMode
{
    /// <summary>Full memory — retain all prior turns (framework default, unbounded).</summary>
    Full = 0,

    /// <summary>Sliding window — retain only the most recent <c>MaxHistory</c> messages.</summary>
    SlidingWindow = 1,

    /// <summary>No memory — each turn runs independently with no prior context.</summary>
    Off = 2,
}
