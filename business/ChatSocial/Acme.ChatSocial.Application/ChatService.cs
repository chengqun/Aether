using Acme.ChatSocial.Domain;
using Acme.Platform.Domain;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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

    public async Task<Conversation> GetOrCreateDirectConversationAsync(Guid targetUserId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/get-or-create-direct-conversation/{targetUserId}", new object());
        return await ParseResponseAsync<Conversation>(response);
    }

    public async Task<Conversation> GetOrCreateGroupConversationAsync(Guid groupId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/get-or-create-group-conversation/{groupId}", new object());
        return await ParseResponseAsync<Conversation>(response);
    }

    public async Task<List<Conversation>> GetMyConversationsAsync()
    {
        return await _httpClient.GetAsync<List<Conversation>>("api/app/chat/my-conversations") ?? new List<Conversation>();
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50)
    {
        return await _httpClient.GetAsync<List<ChatMessage>>($"api/app/chat/messages/{conversationId}?skipCount={skipCount}&maxResultCount={maxResultCount}") ?? new List<ChatMessage>();
    }

    public async Task<ChatMessage> SendMessageAsync(Guid conversationId, string content, string messageType = "Text")
    {
        var response = await _httpClient.PostRawAsync($"api/app/chat/send-message/{conversationId}", new { Content = content, MessageType = messageType });
        return await ParseResponseAsync<ChatMessage>(response);
    }

    public async Task MarkAsReadAsync(Guid conversationId)
    {
        await _httpClient.PostRawAsync($"api/app/chat/mark-as-read/{conversationId}", new object());
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        await _httpClient.GetAsync<object>($"api/app/chat/delete-conversation/{conversationId}");
    }

    public async Task<int> GetUnreadCountAsync()
    {
        var result = await _httpClient.GetAsync<UnreadCountResult>("api/app/chat/unread-count");
        return result?.Count ?? 0;
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }

    private record UnreadCountResult(int Count);
}