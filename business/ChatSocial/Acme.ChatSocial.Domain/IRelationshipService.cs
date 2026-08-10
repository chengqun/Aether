using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IRelationshipService
{
    // 写操作：失败抛异常（调用方已有 try/catch，设计意图即如此）。
    Task<Relationship> CreateRelationshipAsync(int relationshipType, string targetId);
    Task<Relationship> AcceptRelationshipAsync(int relationshipType, string relationshipId);
    Task<Relationship> RejectRelationshipAsync(int relationshipType, string relationshipId);
    Task RemoveRelationshipAsync(int relationshipType, string relationshipId);
    // 读操作：失败返回 null（null=HTTP 失败，空列表=成功无数据——二者必须区分，否则缓存全量替换会误清空）。
    Task<List<Relationship>?> GetAcceptedContactsAsync();
    Task<List<Relationship>?> GetPendingRequestsAsync();

    /// <summary>
    /// 服务端统一搜索所有类型目标（用户/群/股票/设备/服务号）。null=HTTP 失败，空列表=成功无匹配。
    /// 镜像 AlphaAgent <c>SearchAllTargetsAsync</c>——添加好友页据此搜用户/群（client 端无法分类型搜）。
    /// </summary>
    Task<List<SearchTarget>?> SearchAllTargetsAsync(string keyword);
}