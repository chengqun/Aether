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
}