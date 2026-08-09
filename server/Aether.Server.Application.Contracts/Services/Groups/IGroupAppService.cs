using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Groups;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Groups;

public interface IGroupAppService : IApplicationService
{
    /// <summary>创建群聊</summary>
    Task<GroupDto> CreateGroupAsync(CreateGroupDto input);

    /// <summary>获取当前用户所在的所有群聊</summary>
    Task<List<GroupDto>> GetMyGroupsAsync();

    /// <summary>搜索群组</summary>
    Task<List<GroupDto>> SearchGroupsAsync(string keyword);

    /// <summary>获取群组成员列表</summary>
    Task<List<GroupMemberDto>> GetGroupMembersAsync(Guid groupId);

    /// <summary>邀请成员加入群聊</summary>
    Task AddMemberAsync(Guid groupId, AddGroupMemberDto input);

    /// <summary>移除成员</summary>
    Task RemoveMemberAsync(Guid groupId, Guid userId);

    /// <summary>解散群聊</summary>
    Task DisbandGroupAsync(Guid groupId);
}