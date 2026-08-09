using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp;
using Volo.Abp.Identity;

namespace Aether.Server.Domain.Services.Relationships
{
    /// <summary>
    /// 群组关系管理器
    /// 使用AppRelationship统一关系表
    /// </summary>
    public class GroupRelationshipManager : DomainService, IRelationshipManager<AppRelationship, AppGroup, Guid>
    {
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly IRepository<AppGroup, Guid> _groupRepository;

        public RelationshipType Type => RelationshipType.Group;

        private readonly IRepository<IdentityUser, Guid> _userRepository;

        public GroupRelationshipManager(
            IRepository<AppRelationship, Guid> relationshipRepository,
            IRepository<AppGroup, Guid> groupRepository,
            IRepository<IdentityUser, Guid> userRepository)
        {
            _relationshipRepository = relationshipRepository;
            _groupRepository = groupRepository;
            _userRepository = userRepository;
        }

        public async Task<AppRelationship> CreateRelationshipAsync(Guid userId, string targetId)
        {
            AppGroup group;
            if (Guid.TryParse(targetId, out var groupId))
            {
                group = await _groupRepository.FirstOrDefaultAsync(g => g.Id == groupId);
            }
            else
            {
                group = await _groupRepository.FirstOrDefaultAsync(g => g.Name == targetId);
            }

            if (group == null)
            {
                throw new BusinessException("Aether:GroupNotFound");
            }

            // 检查是否已经存在关系
            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString()
            );

            if (existingRelationship != null)
            {
                throw new BusinessException("Aether:AlreadyGroupMember");
            }

            // 根据是否是群主决定关系状态
            // 群主 -> 自动建立关系（Accepted）
            // 普通用户 -> 请求加入（Pending）
            var status = group.OwnerId == userId ? RelationshipStatus.Accepted : RelationshipStatus.Pending;

            var relationship = new AppRelationship(userId, RelationshipType.Group, group.Id.ToString(), status);
            return await _relationshipRepository.InsertAsync(relationship);
        }

        public async Task<AppRelationship> AcceptRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查群组是否存在
            var group = await _groupRepository.FirstOrDefaultAsync(g => g.Id == Guid.Parse(relationship.TargetId));
            if (group == null)
            {
                throw new BusinessException("Aether:GroupNotFound");
            }
            
            // 只有群主才能接受请求
            if (group.OwnerId != currentUserId)
            {
                throw new BusinessException("Aether:NotGroupOwner");
            }
            
            relationship.Accept();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task<AppRelationship> RejectRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查群组是否存在
            var group = await _groupRepository.FirstOrDefaultAsync(g => g.Id == Guid.Parse(relationship.TargetId));
            if (group == null)
            {
                throw new BusinessException("Aether:GroupNotFound");
            }
            
            // 只有群主才能拒绝请求
            if (group.OwnerId != currentUserId)
            {
                throw new BusinessException("Aether:NotGroupOwner");
            }
            
            relationship.Reject();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId)
        {
            await _relationshipRepository.DeleteAsync(relationshipId);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId, Guid userId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查群组是否存在
            var group = await _groupRepository.FirstOrDefaultAsync(g => g.Id == Guid.Parse(relationship.TargetId));
            if (group == null)
            {
                throw new BusinessException("Aether:GroupNotFound");
            }
            
            // 只有群主或关系创建者才能删除关系
            if (group.OwnerId != userId && relationship.UserId != userId)
            {
                throw new BusinessException("Aether:NoPermission");
            }
            
            await _relationshipRepository.DeleteAsync(relationship);
        }

        public async Task<List<AppRelationship>> GetUserRelationshipsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Group && r.Status == RelationshipStatus.Accepted
            );
        }

        public async Task<List<AppRelationship>> GetPendingRequestsAsync(Guid userId)
        {
            // 获取发给当前用户的群组关系请求（用户是群组的所有者）
            var myGroups = await _groupRepository.GetListAsync(g => g.OwnerId == userId);
            var groupIds = myGroups.Select(g => g.Id.ToString()).ToList();

            return await _relationshipRepository.GetListAsync(
                r => groupIds.Contains(r.TargetId) && r.TargetType == RelationshipType.Group && r.Status == RelationshipStatus.Pending
            );
        }

        public async Task<List<AppGroup>> SearchTargetsAsync(string keyword)
        {
            return await _groupRepository.GetListAsync(
                g => g.Name.Contains(keyword)
            );
        }

        public async Task<bool> RelationshipExistsAsync(Guid userId, string targetId)
        {
            return await _relationshipRepository.AnyAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Group && r.TargetId == targetId
            );
        }
    }
}