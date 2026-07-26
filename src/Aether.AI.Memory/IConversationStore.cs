using Aether.AI.Core.Conversations;

namespace Aether.AI.Memory;

/// <summary>
/// Persistence abstraction for <see cref="Conversation"/> history. Separated
/// from <see cref="IMemoryStore"/> because conversations are structured chat
/// transcripts, whereas memory items are distilled knowledge.
/// </summary>
public interface IConversationStore
{
    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Returns lightweight summaries (no full transcript) for list views.</summary>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
