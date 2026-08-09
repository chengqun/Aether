using Acme.ChatSocial.Domain;

namespace Acme.Mobile.Mvvm.Events;

public class NewMessageEvent
{
    public ChatMessage Message { get; }

    public NewMessageEvent(ChatMessage message)
    {
        Message = message;
    }
}