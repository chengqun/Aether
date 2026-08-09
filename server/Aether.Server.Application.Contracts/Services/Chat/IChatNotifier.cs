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
}
