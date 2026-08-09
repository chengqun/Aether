using System;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

public class AppChatMessage : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string MessageType { get; set; } = "Text";
    public DateTime SentAt { get; set; }

    public AppChatMessage() { }

    public AppChatMessage(Guid id, Guid conversationId, Guid senderId, string content, string messageType = "Text")
    {
        Id = id;
        ConversationId = conversationId;
        SenderId = senderId;
        Content = content;
        MessageType = messageType;
        SentAt = DateTime.UtcNow;
    }
}
