using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Application;

public class GroupService : IGroupService
{
    private readonly IAppServerHttpClient _httpClient;

    public GroupService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Group> CreateGroupAsync(string name, string description, List<string>? inviteUsernames = null, CancellationToken ct = default)
    {
        var body = new { Name = name, Description = description, InviteUsernames = inviteUsernames ?? new List<string>() };
        var response = await _httpClient.PostRawAsync("api/app/group/group", body, ct);
        return await ParseResponseAsync<Group>(response, ct);
    }

    public async Task<List<Group>> GetMyGroupsAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetAsync<List<Group>>("api/app/group/my-groups", ct) ?? new List<Group>();
    }

    public async Task<List<GroupMember>> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
    {
        return await _httpClient.GetAsync<List<GroupMember>>($"api/app/group/group-members/{groupId}", ct) ?? new List<GroupMember>();
    }

    public async Task AddMemberAsync(string groupId, string username, CancellationToken ct = default)
    {
        await _httpClient.PostRawAsync($"api/app/group/member/{groupId}", new { Username = username }, ct);
    }

    public async Task RemoveMemberAsync(string groupId, string userId, CancellationToken ct = default)
    {
        await _httpClient.GetAsync<object>($"api/app/group/member?groupId={groupId}&userId={userId}", ct);
    }

    public async Task DisbandGroupAsync(string groupId, CancellationToken ct = default)
    {
        await _httpClient.PostRawAsync($"api/app/group/disband-group/{groupId}", new { }, ct);
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response, CancellationToken ct) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}
