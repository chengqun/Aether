using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Aether.Server.Domain.Services.Relationships;

/// <summary>
/// 服务号关系管理器（单向关注，自动接受，与股票模式一致）
/// </summary>
public class ServiceAccountRelationshipManager : DomainService, IRelationshipManager<AppRelationship, AppServiceAccount, Guid>
{
    private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
    private readonly IRepository<AppServiceAccount, Guid> _serviceAccountRepository;

    public RelationshipType Type => RelationshipType.ServiceAccount;

    public ServiceAccountRelationshipManager(
        IRepository<AppRelationship, Guid> relationshipRepository,
        IRepository<AppServiceAccount, Guid> serviceAccountRepository)
    {
        _relationshipRepository = relationshipRepository;
        _serviceAccountRepository = serviceAccountRepository;
    }

    public async Task<AppRelationship> CreateRelationshipAsync(Guid userId, string targetId)
    {
        // 检查服务号是否存在
        var serviceAccountId = Guid.Parse(targetId);
        var serviceAccount = await _serviceAccountRepository.FirstOrDefaultAsync(sa => sa.Id == serviceAccountId);
        if (serviceAccount == null)
        {
            throw new BusinessException("Aether:ServiceAccountNotFound");
        }

        // 检查是否已关注
        var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
            r => r.UserId == userId && r.TargetType == RelationshipType.ServiceAccount && r.TargetId == targetId
        );

        if (existingRelationship != null)
        {
            throw new BusinessException("Aether:ServiceAccountAlreadyFollowed");
        }

        // 自动接受，无需审批
        var relationship = new AppRelationship(userId, RelationshipType.ServiceAccount, targetId, RelationshipStatus.Accepted);
        return await _relationshipRepository.InsertAsync(relationship);
    }

    public async Task<AppRelationship> AcceptRelationshipAsync(Guid relationshipId, Guid currentUserId)
    {
        var relationship = await _relationshipRepository.GetAsync(relationshipId);
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
            r => r.UserId == userId && r.TargetType == RelationshipType.ServiceAccount
        );
    }

    public async Task<List<AppRelationship>> GetPendingRequestsAsync(Guid userId)
    {
        // 服务号关注自动接受，无待处理请求
        return new List<AppRelationship>();
    }

    public async Task<List<AppServiceAccount>> SearchTargetsAsync(string keyword)
    {
        return await _serviceAccountRepository.GetListAsync(
            sa => sa.Name.Contains(keyword) || (sa.Description != null && sa.Description.Contains(keyword))
        );
    }

    public async Task<bool> RelationshipExistsAsync(Guid userId, string targetId)
    {
        var relationship = await _relationshipRepository.FirstOrDefaultAsync(
            r => r.UserId == userId && r.TargetType == RelationshipType.ServiceAccount && r.TargetId == targetId
        );
        return relationship != null;
    }
}
