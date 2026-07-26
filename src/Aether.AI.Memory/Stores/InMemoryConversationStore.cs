using System.Collections.Concurrent;
using Aether.AI.Core.Conversations;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// In-process implementation of <see cref="IConversationStore"/>.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, Conversation> _conversations = new();

    public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        _conversations[conversation.ConversationId] = conversation;
        return Task.CompletedTask;
    }

    public Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        _conversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation);
    }

    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var results = _conversations.Values
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(
                c.ConversationId, c.Title, c.AgentId, c.AgentName, c.UpdatedAt,
                c.Messages.Count == 0 ? string.Empty : c.Messages[^1].Content))
            .ToList();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>(results);
    }

    public Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var results = _conversations.Values
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Conversation>>(results);
    }

    public Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_conversations.TryRemove(conversationId, out _));
}
