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
