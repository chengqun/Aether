using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp;

namespace Aether.Server.Domain.Services.Relationships
{
    /// <summary>
    /// 股票关系管理器
    /// </summary>
    public class StockRelationshipManager : DomainService, IRelationshipManager<AppRelationship, AppSecurity, Guid>
    {
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly IRepository<AppSecurity, int> _securityRepository;

        public RelationshipType Type => RelationshipType.Stock;

        public StockRelationshipManager(
            IRepository<AppRelationship, Guid> relationshipRepository,
            IRepository<AppSecurity, int> securityRepository)
        {
            _relationshipRepository = relationshipRepository;
            _securityRepository = securityRepository;
        }

        public async Task<AppRelationship> CreateRelationshipAsync(Guid userId, string targetId)
        {
            // 检查股票是否存在（targetId 为数据库主键，避免同 Code 不同 Type 的歧义）
            var security = await _securityRepository.FirstOrDefaultAsync(s => s.Id.ToString() == targetId);
            if (security == null)
            {
                throw new BusinessException("Aether:StockNotFound");
            }

            // 检查是否已经添加
            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Stock && r.TargetId == security.Id.ToString()
            );

            if (existingRelationship != null)
            {
                throw new BusinessException("Aether:StockAlreadyInWatchlist");
            }

            // 添加股票到自选股
            var relationship = new AppRelationship(userId, RelationshipType.Stock, security.Id.ToString(), RelationshipStatus.Accepted);
            return await _relationshipRepository.InsertAsync(relationship);
        }

        public async Task<AppRelationship> AcceptRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            // 只有关系创建者才能接受股票关系请求
            if (relationship.UserId != currentUserId)
            {
                throw new BusinessException("Aether:NotRelationshipOwner");
            }
            relationship.Accept();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task<AppRelationship> RejectRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            // 只有关系创建者才能拒绝股票关系请求
            if (relationship.UserId != currentUserId)
            {
                throw new BusinessException("Aether:NotRelationshipOwner");
            }
            await _relationshipRepository.DeleteAsync(relationship);
            return relationship;
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId)
        {
            await _relationshipRepository.DeleteAsync(relationshipId);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            await _relationshipRepository.DeleteAsync(relationshipId);
        }

        public async Task<List<AppRelationship>> GetUserRelationshipsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Stock
            );
        }

        public async Task<List<AppRelationship>> GetPendingRequestsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Stock && r.Status == RelationshipStatus.Pending
            );
        }

        public async Task<List<AppSecurity>> SearchTargetsAsync(string keyword)
        {
            return await _securityRepository.GetListAsync(
                s => s.Code.Contains(keyword) ||
                     s.Name.Contains(keyword)
            );
        }

        public async Task<bool> RelationshipExistsAsync(Guid userId, string targetId)
        {
            var security = await _securityRepository.FirstOrDefaultAsync(s => s.Id.ToString() == targetId);
            if (security == null)
            {
                return false;
            }

            var relationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Stock && r.TargetId == security.Id.ToString()
            );

            return relationship != null;
        }
    }
}