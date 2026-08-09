using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Acme.ChatSocial.Domain;

namespace Acme.Mobile.Mvvm.Services;

public interface IUnreadMessageCacheService
{
    void CacheMessage(ChatMessage message);
    List<ChatMessage> GetAndClearCache(Guid conversationId);
    List<ChatMessage> PeekCache(Guid conversationId);
    bool HasCachedMessages(Guid conversationId);
}

public class UnreadMessageCacheService : IUnreadMessageCacheService
{
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _cachedMessages = new();

    public void CacheMessage(ChatMessage message)
    {
        _cachedMessages.AddOrUpdate(
            message.ConversationId,
            _ => new List<ChatMessage> { message },
            (_, existing) =>
            {
                existing.Add(message);
                return existing;
            });

        System.Diagnostics.Debug.WriteLine($"[UnreadMessageCache] 缓存消息到 {message.ConversationId}，当前缓存: {_cachedMessages[message.ConversationId].Count} 条");
    }

    public List<ChatMessage> GetAndClearCache(Guid conversationId)
    {
        if (_cachedMessages.TryRemove(conversationId, out var messages))
        {
            System.Diagnostics.Debug.WriteLine($"[UnreadMessageCache] 取出并清除缓存消息 {conversationId}，共 {messages.Count} 条");
            return messages;
        }
        return new List<ChatMessage>();
    }

    public List<ChatMessage> PeekCache(Guid conversationId)
    {
        if (_cachedMessages.TryGetValue(conversationId, out var messages))
        {
            return new List<ChatMessage>(messages);
        }
        return new List<ChatMessage>();
    }

    public bool HasCachedMessages(Guid conversationId)
    {
        return _cachedMessages.ContainsKey(conversationId) && _cachedMessages[conversationId].Count > 0;
    }
}