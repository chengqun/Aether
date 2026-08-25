using System;

namespace Acme.DeviceConnect.Domain;

public class DeviceMessage
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public string SenderName { get; set; } = "未知";
    /// <summary>消息类型（透传服务端 <c>messageType</c>：Text/Progress/ApprovalDecision）。
    /// 默认 "Text" 兼容旧数据。桌面据此路由控制消息（审批决策 / 进度心跳）。</summary>
    public string MessageType { get; set; } = "Text";
}