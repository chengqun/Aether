using Acme.ChatSocial.Domain;
using Acme.Platform.Domain;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Application;

public class GroupService : IGroupService
{
    private readonly IAppServerHttpClient _httpClient;

    public GroupService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Group> CreateGroupAsync(string name, string description, List<string>? inviteUsernames = null)
    {
        var body = new { Name = name, Description = description, InviteUsernames = inviteUsernames ?? new List<string>() };
        var response = await _httpClient.PostRawAsync("api/app/group/group", body);
        return await ParseResponseAsync<Group>(response);
    }

    public async Task<List<Group>> GetMyGroupsAsync()
    {
        return await _httpClient.GetAsync<List<Group>>("api/app/group/my-groups") ?? new List<Group>();
    }

    public async Task<List<GroupMember>> GetGroupMembersAsync(string groupId)
    {
        return await _httpClient.GetAsync<List<GroupMember>>($"api/app/group/group-members/{groupId}") ?? new List<GroupMember>();
    }

    public async Task AddMemberAsync(string groupId, string username)
    {
        await _httpClient.PostRawAsync($"api/app/group/member/{groupId}", new { Username = username });
    }

    public async Task RemoveMemberAsync(string groupId, string userId)
    {
        await _httpClient.GetAsync<object>($"api/app/group/member?groupId={groupId}&userId={userId}");
    }

    public async Task DisbandGroupAsync(string groupId)
    {
        await _httpClient.PostRawAsync($"api/app/group/disband-group/{groupId}", new { });
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}