using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatReducer"/> that keeps only the most recent <c>maxHistory</c>
/// messages, implementing a sliding-window memory policy. Wired into the
/// framework's <c>InMemoryChatHistoryProvider</c> at agent build time.
/// </summary>
public sealed class SlidingWindowChatReducer : IChatReducer
{
    private readonly int _maxHistory;

    public SlidingWindowChatReducer(int maxHistory)
    {
        if (maxHistory < 1) maxHistory = 1;
        _maxHistory = maxHistory;
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var reduced = list.Count <= _maxHistory
            ? list
            : list.Skip(list.Count - _maxHistory).ToList();
        return Task.FromResult<IEnumerable<ChatMessage>>(reduced);
    }
}
