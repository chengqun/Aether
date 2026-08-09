using System;

namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// 服务端通知的新会话事件（仅包含 ConversationId）。
/// 与 NewConversationEvent 不同：后者由客户端本地创建会话时发布（包含完整 Conversation 数据），
/// 本事件由服务端 SignalR NewConversation 通知触发，客户端需要自行从服务端同步完整数据。
/// </summary>
public class NewConversationFromServerEvent
{
    public Guid ConversationId { get; }

    public NewConversationFromServerEvent(Guid conversationId)
    {
        ConversationId = conversationId;
    }
}