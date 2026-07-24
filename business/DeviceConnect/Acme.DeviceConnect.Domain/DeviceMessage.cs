using System;

namespace Acme.DeviceConnect.Domain;

public class DeviceMessage
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public string SenderName { get; set; } = "未知";
}