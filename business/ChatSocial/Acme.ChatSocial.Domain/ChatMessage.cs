using System;
using System.Text.Json.Serialization;

namespace Acme.ChatSocial.Domain;

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string? SenderName { get; set; }
    public string Content { get; set; } = string.Empty;
    public string MessageType { get; set; } = "Text";
    /// <summary>消息时间。服务端 DTO 字段名是 <c>SentAt</c>（非 CreationTime），
    /// REST/SignalR 两条通道都须把 <c>sentAt</c> 映射到这里（否则时间恒为 MinValue，
    /// 且 SignalR 实时反序列化时间字段对不上导致后续时序判断错乱）。</summary>
    [JsonPropertyName("sentAt")]
    public DateTime CreationTime { get; set; }
    public bool IsRead { get; set; }
}