using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Domain;

public interface IRelationshipService
{
    Task<Relationship> CreateRelationshipAsync(int relationshipType, string targetId);
    Task<Relationship> AcceptRelationshipAsync(int relationshipType, string relationshipId);
    Task<Relationship> RejectRelationshipAsync(int relationshipType, string relationshipId);
    Task RemoveRelationshipAsync(int relationshipType, string relationshipId);
    Task<List<Relationship>> GetAcceptedContactsAsync();
    Task<List<Relationship>> GetPendingRequestsAsync();
}