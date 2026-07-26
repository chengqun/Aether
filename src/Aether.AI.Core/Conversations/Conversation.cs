using Aether.AI.Core.Messages;

namespace Aether.AI.Core.Conversations;

/// <summary>
/// A persisted conversation: an ordered sequence of messages associated with
/// a session and (optionally) a user. Conversations are what the Memory layer
/// stores so that past interactions can be recalled later.
/// </summary>
public sealed class Conversation
{
    public Guid ConversationId { get; init; } = Guid.NewGuid();

    public Guid? SessionId { get; init; }

    public string? UserId { get; init; }

    /// <summary>The logical id of the agent bound to this conversation (e.g. a custom agent's Guid).</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Display name of the agent bound to this conversation.</summary>
    public string AgentName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Message> Messages { get; } = new();

    public void Append(Message message)
    {
        Messages.Add(message);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
