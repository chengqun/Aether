using System;

namespace Aether.Server.Application.Contracts.DTOs.Chat;

public class ConversationDto
{
    public Guid Id { get; set; }
    public int Type { get; set; }
    public string? Name { get; set; }
    public string? OtherUserName { get; set; }
    public Guid? OtherUserId { get; set; }
    public string? OtherDeviceId { get; set; }
    public string? DeviceType { get; set; }
    public int UnreadCount { get; set; }
    public string? LastMessage { get; set; }
    public DateTime? LastMessageTime { get; set; }
    public int MemberCount { get; set; }
}

public class ChatMessageDto
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string MessageType { get; set; } = "Text";
    public DateTime SentAt { get; set; }
    public bool IsMine { get; set; }
}

public class SendMessageDto
{
    public string Content { get; set; } = string.Empty;
    public string MessageType { get; set; } = "Text";
}
