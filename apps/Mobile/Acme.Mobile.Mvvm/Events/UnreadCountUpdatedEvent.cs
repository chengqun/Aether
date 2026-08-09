using System;

namespace Acme.Mobile.Mvvm.Events;

public class UnreadCountUpdatedEvent
{
    public Guid ConversationId { get; }
    public Guid UserId { get; }
    public int UnreadCount { get; }

    public UnreadCountUpdatedEvent(Guid conversationId, Guid userId, int unreadCount)
    {
        ConversationId = conversationId;
        UserId = userId;
        UnreadCount = unreadCount;
    }
}