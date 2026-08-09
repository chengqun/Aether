using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Relationships;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Relationships;

/// <summary>
/// 关系服务接口
/// 统一处理不同类型的关系管理
/// </summary>
public interface IRelationshipService : IApplicationService
{
    /// <summary>
    /// 建立关系
    /// </summary>
    Task<RelationshipDto> CreateRelationshipAsync(RelationshipType type, string targetId);

    /// <summary>
    /// 接受关系请求
    /// </summary>
    Task<RelationshipDto> AcceptRelationshipAsync(RelationshipType type, string relationshipId);

    /// <summary>
    /// 拒绝关系请求
    /// </summary>
    Task<RelationshipDto> RejectRelationshipAsync(RelationshipType type, string relationshipId);

    /// <summary>
    /// 移除关系
    /// </summary>
    Task RemoveRelationshipAsync(RelationshipType type, string relationshipId);

    /// <summary>
    /// 搜索所有类型的目标对象
    /// </summary>
    Task<List<TargetDto>> SearchAllTargetsAsync(string keyword);

    /// <summary>
    /// 获取用户已接受的联系人（通讯录）
    /// </summary>
    Task<ContactBookDto> GetAcceptedContactsAsync();

    /// <summary>
    /// 获取用户待处理的请求（新的朋友）
    /// </summary>
    Task<ContactBookDto> GetPendingRequestsAsync();
}