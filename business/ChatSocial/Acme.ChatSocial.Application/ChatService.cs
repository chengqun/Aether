using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Application;

public class ChatService : IChatService
{
    private readonly IAppServerHttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ChatService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Conversation> GetOrCreateDirectConversationAsync(Guid targetUserId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/get-or-create-direct-conversation/{targetUserId}", new object(), ct);
        return await ParseResponseAsync<Conversation>(response, ct);
    }

    public async Task<Conversation> GetOrCreateGroupConversationAsync(Guid groupId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/get-or-create-group-conversation/{groupId}", new object(), ct);
        return await ParseResponseAsync<Conversation>(response, ct);
    }

    public async Task<List<Conversation>> GetMyConversationsAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetAsync<List<Conversation>>("api/app/chat/my-conversations", ct) ?? new List<Conversation>();
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50, CancellationToken ct = default)
    {
        return await _httpClient.GetAsync<List<ChatMessage>>($"api/app/chat/messages/{conversationId}?skipCount={skipCount}&maxResultCount={maxResultCount}", ct) ?? new List<ChatMessage>();
    }

    public async Task<ChatMessage> SendMessageAsync(Guid conversationId, string content, string messageType = "Text", CancellationToken ct = default)
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/send-message/{conversationId}", new { Content = content, MessageType = messageType }, ct);
        return await ParseResponseAsync<ChatMessage>(response, ct);
    }

    public async Task MarkAsReadAsync(Guid conversationId, CancellationToken ct = default)
    {
        await _httpClient.PostRawAsync($"api/app/chat/mark-as-read/{conversationId}", new object(), ct);
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        await _httpClient.GetAsync<object>($"api/app/chat/delete-conversation/{conversationId}", ct);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        var result = await _httpClient.GetAsync<UnreadCountResult>("api/app/chat/unread-count", ct);
        return result?.Count ?? 0;
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response, CancellationToken ct) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }

    private record UnreadCountResult(int Count);
}
