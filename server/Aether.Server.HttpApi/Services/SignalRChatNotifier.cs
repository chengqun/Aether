using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.HttpApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aether.Server.HttpApi.Services;

public class SignalRChatNotifier : IChatNotifier
{
    private readonly IHubContext<ChatHub> _hubContext;

    public SignalRChatNotifier(IHubContext<ChatHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyMessageAsync(Guid conversationId, ChatMessageDto message, Guid senderId)
    {
        // 广播给会话组中所有连接（发送者的回声由客户端根据 SenderId 过滤）
        await _hubContext.Clients.Group($"conv_{conversationId}")
            .SendAsync("ReceiveMessage", message);
    }

    public async Task NotifyUnreadCountUpdatedAsync(Guid conversationId, Guid userId, int unreadCount)
    {
        await _hubContext.Clients.Group($"conv_{conversationId}")
            .SendAsync("UnreadCountUpdated", conversationId, userId, unreadCount);
    }

    public async Task NotifyNewConversationAsync(Guid conversationId, Guid targetUserId)
    {
        // 向目标用户发送新会话通知，客户端收到后自动加入 SignalR 组并同步会话数据
        await _hubContext.Clients.User(targetUserId.ToString())
            .SendAsync("NewConversation", conversationId);
    }
}
