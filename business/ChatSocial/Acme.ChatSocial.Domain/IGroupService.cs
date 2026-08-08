using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IGroupService
{
    Task<Group> CreateGroupAsync(string name, string description, List<string>? inviteUsernames = null, CancellationToken ct = default);
    Task<List<Group>> GetMyGroupsAsync(CancellationToken ct = default);
    Task<List<GroupMember>> GetGroupMembersAsync(string groupId, CancellationToken ct = default);
    Task AddMemberAsync(string groupId, string username, CancellationToken ct = default);
    Task RemoveMemberAsync(string groupId, string userId, CancellationToken ct = default);
    Task DisbandGroupAsync(string groupId, CancellationToken ct = default);
}
