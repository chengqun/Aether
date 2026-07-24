using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IGroupService
{
    Task<Group> CreateGroupAsync(string name, string description, List<string>? inviteUsernames = null);
    Task<List<Group>> GetMyGroupsAsync();
    Task<List<GroupMember>> GetGroupMembersAsync(string groupId);
    Task AddMemberAsync(string groupId, string username);
    Task RemoveMemberAsync(string groupId, string userId);
    Task DisbandGroupAsync(string groupId);
}