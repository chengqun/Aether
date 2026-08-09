using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Groups;
using Aether.Server.Application.Contracts.Services.Groups;
using Aether.Server.Domain.Services.Groups;
using Aether.Server.Domain.Services.Relationships;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.Uow;
using Volo.Abp.Features;
using Microsoft.AspNetCore.Authorization;

namespace Aether.Server.Application.Services.Groups
{
    [Authorize]
    [RequiresFeature("Chat.Group")]
    [Route("api/app/group")]
    public class GroupAppService : ApplicationService, IGroupAppService
    {
        private readonly IGroupManager _groupManager;
        private readonly IRelationshipManager<AppRelationship, AppGroup, Guid> _groupRelationshipManager;
        private readonly IRepository<IdentityUser, Guid> _userRepository;
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;

        public GroupAppService(
            IGroupManager groupManager,
            IRelationshipManager<AppRelationship, AppGroup, Guid> groupRelationshipManager,
            IRepository<IdentityUser, Guid> userRepository,
            IRepository<AppRelationship, Guid> relationshipRepository)
        {
            _groupManager = groupManager;
            _groupRelationshipManager = groupRelationshipManager;
            _userRepository = userRepository;
            _relationshipRepository = relationshipRepository;
        }

        [UnitOfWork]
        [HttpPost("group")]
        public async Task<GroupDto> CreateGroupAsync(CreateGroupDto input)
        {
            if (!CurrentUser.IsAuthenticated)
                throw new UserFriendlyException("未登录");

            var isAdmin = CurrentUser.Roles.Contains("admin");
            if (!isAdmin)
                throw new UserFriendlyException("只有管理员才能创建群聊");

            var ownerId = CurrentUser.Id!.Value;
            var ownerUser = await _userRepository.GetAsync(ownerId);

            var group = await _groupManager.CreateGroupAsync(input.Name, ownerId, input.Description);

            if (input.InviteUsernames?.Any() == true)
            {
                foreach (var username in input.InviteUsernames)
                {
                    var invitedUser = await _userRepository.FirstOrDefaultAsync(u => u.UserName == username);
                    if (invitedUser != null)
                    {
                        var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                            r => r.UserId == invitedUser.Id && r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString()
                        );
                        if (existingRelationship == null)
                        {
                            var relationship = new AppRelationship(invitedUser.Id, RelationshipType.Group, group.Id.ToString(), RelationshipStatus.Accepted);
                            await _relationshipRepository.InsertAsync(relationship);
                        }
                    }
                }
            }

            var members = await _relationshipRepository.GetListAsync(
                r => r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString() && r.Status == RelationshipStatus.Accepted
            );
            return new GroupDto
            {
                Id = group.Id,
                Name = group.Name,
                Description = group.Description,
                OwnerId = group.OwnerId,
                OwnerUsername = ownerUser.UserName,
                MemberCount = members.Count,
                CreationTime = group.CreationTime
            };
        }

        [HttpGet("my-groups")]
        public async Task<List<GroupDto>> GetMyGroupsAsync()
        {
            if (!CurrentUser.IsAuthenticated)
                return new List<GroupDto>();

            var userId = CurrentUser.Id!.Value;
            var relationships = await _relationshipRepository.GetListAsync(
                r => r.UserId == userId &&
                     r.TargetType == RelationshipType.Group &&
                     r.Status == RelationshipStatus.Accepted
            );

            var dtos = new List<GroupDto>();
            foreach (var relationship in relationships)
            {
                var group = await _groupManager.GetGroupAsync(Guid.Parse(relationship.TargetId));
                if (group != null)
                {
                    var ownerUser = await _userRepository.FirstOrDefaultAsync(u => u.Id == group.OwnerId);
                    var members = await _relationshipRepository.GetListAsync(
                        r => r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString() && r.Status == RelationshipStatus.Accepted
                    );
                    dtos.Add(new GroupDto
                    {
                        Id = group.Id,
                        Name = group.Name,
                        Description = group.Description,
                        OwnerId = group.OwnerId,
                        OwnerUsername = ownerUser?.UserName ?? string.Empty,
                        MemberCount = members.Count,
                        CreationTime = group.CreationTime
                    });
                }
            }
            return dtos;
        }

        [HttpGet("search-groups")]
        public async Task<List<GroupDto>> SearchGroupsAsync(string keyword)
        {
            if (!CurrentUser.IsAuthenticated)
                return new List<GroupDto>();

            var groups = await _groupManager.SearchGroupsAsync(keyword);
            var dtos = new List<GroupDto>();
            foreach (var group in groups)
            {
                var ownerUser = await _userRepository.FirstOrDefaultAsync(u => u.Id == group.OwnerId);
                var members = await _relationshipRepository.GetListAsync(
                    r => r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString() && r.Status == RelationshipStatus.Accepted
                );
                dtos.Add(new GroupDto
                {
                    Id = group.Id,
                    Name = group.Name,
                    Description = group.Description,
                    OwnerId = group.OwnerId,
                    OwnerUsername = ownerUser?.UserName ?? string.Empty,
                    MemberCount = members.Count,
                    CreationTime = group.CreationTime
                });
            }
            return dtos;
        }

        [HttpGet("group-members/{groupId}")]
        public async Task<List<GroupMemberDto>> GetGroupMembersAsync(Guid groupId)
        {
            await EnsureMemberAsync(groupId);
            var group = await _groupManager.GetGroupAsync(groupId);

            var memberList = new List<GroupMemberDto>();

            var ownerUser = await _userRepository.FirstOrDefaultAsync(u => u.Id == group.OwnerId);
            memberList.Add(new GroupMemberDto
            {
                Id = Guid.Empty,
                GroupId = groupId,
                UserId = group.OwnerId,
                Username = ownerUser?.UserName ?? string.Empty,
                Role = "Owner",
                JoinedAt = group.CreationTime
            });

            var memberRelationships = await _relationshipRepository.GetListAsync(
                r => r.TargetType == RelationshipType.Group &&
                     r.TargetId == groupId.ToString() &&
                     r.Status == RelationshipStatus.Accepted &&
                     r.UserId != group.OwnerId
            );

            foreach (var rel in memberRelationships)
            {
                var user = await _userRepository.GetAsync(rel.UserId);
                memberList.Add(new GroupMemberDto
                {
                    Id = rel.Id,
                    GroupId = groupId,
                    UserId = rel.UserId,
                    Username = user.UserName,
                    Role = "Member",
                    JoinedAt = rel.CreationTime
                });
            }

            return memberList;
        }

        [HttpPost("member/{groupId}")]
        public async Task AddMemberAsync(Guid groupId, AddGroupMemberDto input)
        {
            var currentUserId = CurrentUser.Id ?? throw new UserFriendlyException("未登录");
            await EnsureAdminOrOwnerAsync(groupId);
            var user = await _userRepository.FirstOrDefaultAsync(u => u.UserName == input.Username);
            if (user == null)
                throw new UserFriendlyException($"用户 '{input.Username}' 不存在");

            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == user.Id && r.TargetType == RelationshipType.Group && r.TargetId == groupId.ToString()
            );

            if (existingRelationship != null)
            {
                throw new BusinessException("Aether:AlreadyGroupMember");
            }

            var relationship = new AppRelationship(user.Id, RelationshipType.Group, groupId.ToString(), RelationshipStatus.Accepted);
            await _relationshipRepository.InsertAsync(relationship);
        }

        [HttpDelete("member")]
        public async Task RemoveMemberAsync(Guid groupId, Guid userId)
        {
            var currentUserId = CurrentUser.Id ?? throw new UserFriendlyException("未登录");

            if (currentUserId == userId)
            {
                var relationships = await _relationshipRepository.GetListAsync(
                    r => r.UserId == currentUserId && r.TargetType == RelationshipType.Group && r.TargetId == groupId.ToString()
                );
                if (relationships.Any())
                {
                    await _relationshipRepository.DeleteAsync(relationships.First());
                }
                return;
            }

            await EnsureAdminOrOwnerAsync(groupId);
            var relationships2 = await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Group && r.TargetId == groupId.ToString()
            );
            if (relationships2.Any())
            {
                await _relationshipRepository.DeleteAsync(relationships2.First());
            }
        }

        [HttpPost("disband-group/{groupId}")]
        public async Task DisbandGroupAsync(Guid groupId)
        {
            await EnsureOwnerAsync(groupId);

            // 删除所有群成员的关系记录
            var allRelationships = await _relationshipRepository.GetListAsync(
                r => r.TargetType == RelationshipType.Group && r.TargetId == groupId.ToString()
            );
            foreach (var rel in allRelationships)
            {
                await _relationshipRepository.DeleteAsync(rel);
            }

            // 删除 AppGroup 实体
            await _groupManager.DisbandGroupAsync(groupId);
        }

        private async Task EnsureMemberAsync(Guid groupId)
        {
            var userId = CurrentUser.Id ?? throw new UserFriendlyException("未登录");
            var group = await _groupManager.GetGroupAsync(groupId);
            
            if (group == null)
                throw new UserFriendlyException("群组不存在");

            if (group.OwnerId != userId)
            {
                var relationship = await _relationshipRepository.FirstOrDefaultAsync(
                    r => r.UserId == userId && r.TargetType == RelationshipType.Group && r.TargetId == groupId.ToString() && r.Status == RelationshipStatus.Accepted
                );
                if (relationship == null)
                    throw new UserFriendlyException("您不是该群聊的成员");
            }
        }

        private async Task EnsureAdminOrOwnerAsync(Guid groupId)
        {
            var userId = CurrentUser.Id ?? throw new UserFriendlyException("未登录");
            var group = await _groupManager.GetGroupAsync(groupId);
            if (group == null)
                throw new UserFriendlyException("群组不存在");

            if (group.OwnerId != userId)
                throw new UserFriendlyException("只有群主才能执行此操作");
        }

        private async Task EnsureOwnerAsync(Guid groupId)
        {
            var userId = CurrentUser.Id ?? throw new UserFriendlyException("未登录");
            var group = await _groupManager.GetGroupAsync(groupId);
            if (group == null)
                throw new UserFriendlyException("群组不存在");

            if (group.OwnerId != userId)
                throw new UserFriendlyException("只有群主才能执行此操作");
        }
    }
}