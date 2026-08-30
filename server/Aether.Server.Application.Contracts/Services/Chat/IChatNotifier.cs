using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;

namespace Aether.Server.Application.Contracts.Services.Chat;

/// <summary>
/// 聊天通知抽象：Application 层通过此接口发送实时通知，
/// HttpApi 层用 IHubContext 实现，避免 Application 依赖 HttpApi。
/// </summary>
public interface IChatNotifier
{
    Task NotifyMessageAsync(Guid conversationId, ChatMessageDto message, Guid senderId);
    Task NotifyUnreadCountUpdatedAsync(Guid conversationId, Guid userId, int unreadCount);
    Task NotifyNewConversationAsync(Guid conversationId, Guid targetUserId);

    /// <summary>
    /// 把指定设备的所有已连接 SignalR 连接加入会话组 <c>conv_{conversationId}</c>。
    /// <para>关闭竞态：桌面（设备连接）先连、手机后建会话时，桌面不在组里收不到消息。设备经
    /// authorizationCode 连接、无 sub claim，<c>Clients.User</c> 触达不了，故需由追踪器主动把已连接的
    /// 设备 connectionId 拉进组。在 <c>GetOrCreateDeviceConversationAsync</c>（手机 ensure 会话时）调用，
    /// 确保发首条消息前桌面已在组里。</para>
    /// </summary>
    Task EnsureDeviceConnectionsJoinedAsync(Guid conversationId, Guid deviceId);
}
