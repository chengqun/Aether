using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IChatService
{
    Task<Conversation> GetOrCreateDirectConversationAsync(Guid targetUserId, CancellationToken ct = default);
    Task<Conversation> GetOrCreateGroupConversationAsync(Guid groupId, CancellationToken ct = default);
    Task<List<Conversation>> GetMyConversationsAsync(CancellationToken ct = default);
    Task<List<ChatMessage>> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50, CancellationToken ct = default);
    Task<ChatMessage> SendMessageAsync(Guid conversationId, string content, string messageType = "Text", CancellationToken ct = default);
    Task MarkAsReadAsync(Guid conversationId, CancellationToken ct = default);
    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);
}
