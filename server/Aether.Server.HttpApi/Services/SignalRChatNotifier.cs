using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.HttpApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aether.Server.HttpApi.Services;

public class SignalRChatNotifier : IChatNotifier
{
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly IDeviceConnectionTracker _deviceConnectionTracker;

    public SignalRChatNotifier(IHubContext<ChatHub> hubContext, IDeviceConnectionTracker deviceConnectionTracker)
    {
        _hubContext = hubContext;
        _deviceConnectionTracker = deviceConnectionTracker;
    }

    public async Task NotifyMessageAsync(Guid conversationId, ChatMessageDto message, Guid senderId)
    {
        // 设备发送方排除其自身连接，避免桌面端收到自己的回声。
        // 此前广播给全组含发送者，桌面端靠 SignalRDeviceChatClient 的 _recentSentMessageIds 去重，
        // 但该 LRU 有竞态：服务端在 SendMessage 返回前已广播，echo 可在客户端登记 sentId 之前到达→
        // 漏过→桌面把自己的回复当新消息重跑 agent→发新回复→死循环。
        // 服务端不回广播给发送者从根上消除竞态。
        // 设备连接登记在 _deviceConnectionTracker；用户连接不在其中（GetConnections 返回空→走全量广播），
        // 用户发送方（手机经 REST SendMessageAsync）的自回声仍由手机端客户端按 SenderId 过滤，行为不变。
        var groupName = $"conv_{conversationId}";
        var excluded = _deviceConnectionTracker.GetConnections(senderId);
        if (excluded.Count > 0)
            await _hubContext.Clients.GroupExcept(groupName, excluded.ToArray())
                .SendAsync("ReceiveMessage", message);
        else
            await _hubContext.Clients.Group(groupName)
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

    public async Task EnsureDeviceConnectionsJoinedAsync(Guid conversationId, Guid deviceId)
    {
        // 设备经 authorizationCode 连接（无 sub claim）→ Clients.User 触达不了，必须经追踪器拿 connectionId
        // 主动加组。幂等：AddToGroupAsync 重复加同一 connectionId+group 无副作用。
        var connectionIds = _deviceConnectionTracker.GetConnections(deviceId);
        foreach (var connectionId in connectionIds)
        {
            await _hubContext.Groups.AddToGroupAsync(connectionId, $"conv_{conversationId}");
        }
    }
}
