using System;
using Volo.Abp.Domain.Entities;

namespace Aether.Server.Domain.Entities;

public class AppConversationParticipant : Entity<Guid>
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastReadAt { get; set; }
    public string Role { get; set; } = "Member";
    public DateTime JoinedAt { get; set; }

    public AppConversationParticipant() { }

    public AppConversationParticipant(Guid id, Guid conversationId, Guid userId, string role = "Member")
    {
        Id = id;
        ConversationId = conversationId;
        UserId = userId;
        Role = role;
        JoinedAt = DateTime.UtcNow;
    }

    public void MarkAsRead()
    {
        UnreadCount = 0;
        LastReadAt = DateTime.UtcNow;
    }

    public void IncrementUnread() => UnreadCount++;
}
