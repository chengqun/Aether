using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;

namespace Aether.AI.Memory;

/// <summary>
/// Persistence abstraction for <see cref="Conversation"/> history. The transcript is a typed,
/// append-only event stream (<see cref="AgentRun"/> + <see cref="AgentEvent"/>), NOT a flat message
/// list on <see cref="Conversation"/>. This replaces the old <c>SaveAsync(Conversation)</c> with
/// embedded messages: structural invariants (call↔result pairing, role alternation) now hold by
/// construction at write time, so the cross-restart restore path needs no sanitize saga.
/// <para>
/// Callers create/update conversation <em>metadata</em> via <see cref="SaveConversationAsync"/>, then
/// append each completed turn as a run+events batch via <see cref="AppendRunAsync"/> (which also
/// denormalizes the conversation's preview/updatedAt). Resume reads runs+events back.
/// </para>
/// </summary>
public interface IConversationStore
{
    /// <summary>Save/update conversation metadata only (title/agent/preview/updatedAt). No transcript.</summary>
    Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Load conversation metadata only (no runs/events). Null if not found.</summary>
    Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Returns lightweight summaries (no transcript) for list views, reading denormalized preview.</summary>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns metadata-only conversations for a user (no transcript loaded).</summary>
    Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically append a run and its events, and update the parent conversation's
    /// <see cref="Conversation.LastMessagePreview"/>/<see cref="Conversation.LastMessageAt"/>/<see cref="Conversation.UpdatedAt"/>.
    /// The conversation row must already exist (call <see cref="SaveConversationAsync"/> first).
    /// </summary>
    Task AppendRunAsync(AgentRun run, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken = default);

    /// <summary>All runs of a conversation, ordered by <see cref="AgentRun.Seq"/> (oldest first).</summary>
    Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>All events of a run, ordered by <see cref="AgentEvent.Seq"/> (oldest first).</summary>
    Task<IReadOnlyList<AgentEvent>> GetEventsAsync(Guid runId, CancellationToken cancellationToken = default);
}
