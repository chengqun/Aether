using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Application;

public class RelationshipService : IRelationshipService
{
    private readonly IAppServerHttpClient _httpClient;

    public RelationshipService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Relationship> CreateRelationshipAsync(int relationshipType, string targetId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/relationship/{targetId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task<Relationship> AcceptRelationshipAsync(int relationshipType, string relationshipId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/accept-relationship/{relationshipId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task<Relationship> RejectRelationshipAsync(int relationshipType, string relationshipId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/reject-relationship/{relationshipId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task RemoveRelationshipAsync(int relationshipType, string relationshipId)
    {
        await _httpClient.GetAsync<object>($"api/app/relationship/relationship/{relationshipId}?type={relationshipType}");
    }

    public async Task<List<Relationship>> GetAcceptedContactsAsync()
    {
        return await _httpClient.GetAsync<List<Relationship>>("api/app/relationship/accepted-contacts") ?? new List<Relationship>();
    }

    public async Task<List<Relationship>> GetPendingRequestsAsync()
    {
        return await _httpClient.GetAsync<List<Relationship>>("api/app/relationship/pending-requests") ?? new List<Relationship>();
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}