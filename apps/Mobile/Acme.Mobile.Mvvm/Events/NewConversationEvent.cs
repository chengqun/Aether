using Acme.ChatSocial.Domain;

namespace Acme.Mobile.Mvvm.Events;

public class NewConversationEvent
{
    public Conversation Conversation { get; }

    public NewConversationEvent(Conversation conversation)
    {
        Conversation = conversation;
    }
}