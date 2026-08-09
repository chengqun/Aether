using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IChatService
{
    /// <summary>失败返回 null（HTTP 失败/非 2xx），成功返回会话（可能新建）。调用方用 null 判断失败。</summary>
    Task<Conversation?> GetOrCreateDirectConversationAsync(Guid targetUserId, CancellationToken ct = default);
    Task<Conversation?> GetOrCreateGroupConversationAsync(Guid groupId, CancellationToken ct = default);
    /// <summary>失败返回 null，成功返回列表（可能空）。null=失败，空列表=成功无数据——二者必须区分（否则缓存同步会误清空）。</summary>
    Task<List<Conversation>?> GetMyConversationsAsync(CancellationToken ct = default);
    Task<List<ChatMessage>?> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50, CancellationToken ct = default);
    /// <summary>失败返回 null（调用方据此恢复输入文本 + 报错，非显示空气泡）。</summary>
    Task<ChatMessage?> SendMessageAsync(Guid conversationId, string content, string messageType = "Text", CancellationToken ct = default);
    Task MarkAsReadAsync(Guid conversationId, CancellationToken ct = default);
    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);
}
