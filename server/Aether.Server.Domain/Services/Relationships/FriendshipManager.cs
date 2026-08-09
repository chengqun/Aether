using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Identity;
using Volo.Abp;

namespace Aether.Server.Domain.Services.Relationships
{
    /// <summary>
    /// 好友关系管理器
    /// </summary>
    public class FriendshipManager : DomainService, IRelationshipManager<AppRelationship, IdentityUser, Guid>
    {
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly IRepository<IdentityUser, Guid> _userRepository;

        public RelationshipType Type => RelationshipType.Friendship;

        public FriendshipManager(
            IRepository<AppRelationship, Guid> relationshipRepository,
            IRepository<IdentityUser, Guid> userRepository)
        {
            _relationshipRepository = relationshipRepository;
            _userRepository = userRepository;
        }

        public async Task<AppRelationship> CreateRelationshipAsync(Guid userId, string targetId)
        {
            var friendUserId = Guid.Parse(targetId);

            // 检查用户是否存在
            var friendUser = await _userRepository.FirstOrDefaultAsync(u => u.Id == friendUserId);
            if (friendUser == null)
            {
                throw new BusinessException("Aether:UserNotFound")
                    .WithData("UserId", targetId);
            }

            // 检查是否已经存在正向好友关系
            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Friendship && r.TargetId == targetId
            );

            if (existingRelationship != null)
            {
                // 如果存在的是已接受的关系，说明已经是好友
                if (existingRelationship.Status == RelationshipStatus.Accepted)
                {
                    throw new BusinessException("Aether:FriendshipExists");
                }
                // 如果存在的是待处理的关系，可以直接返回
                return existingRelationship;
            }

            // 检查反向关系（对方是否已经发过好友请求给我，或者已经是好友）
            var reverseRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == friendUserId && r.TargetType == RelationshipType.Friendship && r.TargetId == userId.ToString()
            );

            if (reverseRelationship != null)
            {
                // 如果对方已经是我的好友（Accepted），我不能再添加他为好友
                if (reverseRelationship.Status == RelationshipStatus.Accepted)
                {
                    throw new BusinessException("Aether:FriendshipExists");
                }
                
                // 如果对方发的是待处理请求，先删除旧的，然后允许创建新请求
                // 这样单向删除后可以重新添加
                if (reverseRelationship.Status == RelationshipStatus.Pending)
                {
                    await _relationshipRepository.DeleteAsync(reverseRelationship);
                }
            }

            // 创建好友请求
            var relationship = new AppRelationship(userId, RelationshipType.Friendship, targetId, RelationshipStatus.Pending);
            return await _relationshipRepository.InsertAsync(relationship);
        }

        public async Task<AppRelationship> AcceptRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            // 只有目标用户（被请求方）才能接受好友请求
            if (relationship.TargetId != currentUserId.ToString())
            {
                throw new BusinessException("Aether:NotTargetUser");
            }
            
            // 接受好友请求
            relationship.Accept();
            await _relationshipRepository.UpdateAsync(relationship);
            
            // 创建反向关系（从被请求方到请求方）
            var reverseRelationship = new AppRelationship(
                currentUserId, 
                RelationshipType.Friendship, 
                relationship.UserId.ToString(), 
                RelationshipStatus.Accepted
            );
            await _relationshipRepository.InsertAsync(reverseRelationship);
            
            return relationship;
        }

        public async Task<AppRelationship> RejectRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            // 只有目标用户（被请求方）才能拒绝好友请求
            if (relationship.TargetId != currentUserId.ToString())
            {
                throw new BusinessException("Aether:NotTargetUser");
            }
            relationship.Reject();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId)
        {
            await _relationshipRepository.DeleteAsync(relationshipId);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 删除正向关系
            await _relationshipRepository.DeleteAsync(relationship);
            
            // 删除反向关系
            var reverseRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == Guid.Parse(relationship.TargetId) &&
                     r.TargetId == relationship.UserId.ToString() &&
                     r.TargetType == RelationshipType.Friendship
            );
            
            if (reverseRelationship != null)
            {
                await _relationshipRepository.DeleteAsync(reverseRelationship);
            }
        }

        public async Task<List<AppRelationship>> GetUserRelationshipsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.UserId == userId &&
                     r.TargetType == RelationshipType.Friendship &&
                     r.Status == RelationshipStatus.Accepted
            );
        }

        public async Task<List<AppRelationship>> GetPendingRequestsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.TargetId == userId.ToString() &&
                     r.TargetType == RelationshipType.Friendship &&
                     r.Status == RelationshipStatus.Pending
            );
        }

        public async Task<List<IdentityUser>> SearchTargetsAsync(string keyword)
        {
            return await _userRepository.GetListAsync(
                u => u.UserName.Contains(keyword) ||
                     u.Email.Contains(keyword)
            );
        }

        public async Task<bool> RelationshipExistsAsync(Guid userId, string targetId)
        {
            var relationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId &&
                     r.TargetType == RelationshipType.Friendship &&
                     r.TargetId == targetId &&
                     r.Status == RelationshipStatus.Accepted
            );

            return relationship != null;
        }
    }
}