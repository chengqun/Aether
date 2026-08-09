using System;

namespace Acme.Mobile.Mvvm.Events;

public class ConversationReadEvent
{
    public Guid ConversationId { get; set; }

    public ConversationReadEvent(Guid conversationId)
    {
        ConversationId = conversationId;
    }
}