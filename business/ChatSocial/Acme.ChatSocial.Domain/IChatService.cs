using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IChatService
{
    Task<Conversation> GetOrCreateDirectConversationAsync(Guid targetUserId);
    Task<Conversation> GetOrCreateGroupConversationAsync(Guid groupId);
    Task<List<Conversation>> GetMyConversationsAsync();
    Task<List<ChatMessage>> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50);
    Task<ChatMessage> SendMessageAsync(Guid conversationId, string content, string messageType = "Text");
    Task MarkAsReadAsync(Guid conversationId);
    Task DeleteConversationAsync(Guid conversationId);
    Task<int> GetUnreadCountAsync();
}