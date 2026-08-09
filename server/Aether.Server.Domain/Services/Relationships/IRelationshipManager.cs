using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;

namespace Aether.Server.Domain.Services.Relationships
{
    /// <summary>
    /// 关系管理接口
    /// 定义所有关系类型的统一行为
    /// </summary>
    /// <typeparam name="TRelationship">关系实体类型</typeparam>
    /// <typeparam name="TEntity">关联实体类型</typeparam>
    /// <typeparam name="TId">关系ID类型</typeparam>
    public interface IRelationshipManager<TRelationship, TEntity, TId>
        where TRelationship : class
        where TEntity : class
    {
        /// <summary>
        /// 关系类型
        /// </summary>
        RelationshipType Type { get; }

        /// <summary>
        /// 建立关系
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <param name="targetId">目标对象ID</param>
        /// <returns></returns>
        Task<TRelationship> CreateRelationshipAsync(Guid userId, string targetId);

        /// <summary>
        /// 接受关系请求
        /// </summary>
        /// <param name="relationshipId">关系ID</param>
        /// <param name="currentUserId">当前用户ID</param>
        /// <returns></returns>
        Task<TRelationship> AcceptRelationshipAsync(TId relationshipId, Guid currentUserId);

        /// <summary>
        /// 拒绝关系请求
        /// </summary>
        /// <param name="relationshipId">关系ID</param>
        /// <param name="currentUserId">当前用户ID</param>
        /// <returns></returns>
        Task<TRelationship> RejectRelationshipAsync(TId relationshipId, Guid currentUserId);

        /// <summary>
        /// 移除关系
        /// </summary>
        /// <param name="relationshipId">关系ID</param>
        /// <returns></returns>
        Task RemoveRelationshipAsync(TId relationshipId);

        /// <summary>
        /// 移除关系（带当前用户ID）
        /// </summary>
        /// <param name="relationshipId">关系ID</param>
        /// <param name="currentUserId">当前用户ID</param>
        /// <returns></returns>
        Task RemoveRelationshipAsync(TId relationshipId, Guid currentUserId);

        /// <summary>
        /// 获取用户的所有关系
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <returns></returns>
        Task<List<TRelationship>> GetUserRelationshipsAsync(Guid userId);

        /// <summary>
        /// 获取用户的待处理关系请求
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <returns></returns>
        Task<List<TRelationship>> GetPendingRequestsAsync(Guid userId);

        /// <summary>
        /// 搜索目标对象
        /// </summary>
        /// <param name="keyword">搜索关键词</param>
        /// <returns></returns>
        Task<List<TEntity>> SearchTargetsAsync(string keyword);

        /// <summary>
        /// 检查关系是否存在
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <param name="targetId">目标对象ID</param>
        /// <returns></returns>
        Task<bool> RelationshipExistsAsync(Guid userId, string targetId);
    }
}