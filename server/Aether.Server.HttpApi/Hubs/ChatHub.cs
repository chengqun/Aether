using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.Domain.Services.Chat;
using Aether.Server.Domain.Services.Devices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Aether.Server.HttpApi.Hubs;

/// <summary>
/// 聊天 Hub：支持用户 JWT 认证和设备授权码认证两种连接方式。
/// 用户通过 access_token 连接，设备通过 authorizationCode 查询参数连接。
/// </summary>
public class ChatHub : Hub
{
    private readonly IConversationManager _conversationManager;
    private readonly IChatAppService _chatAppService;
    private readonly IChatNotifier _chatNotifier;
    private readonly IDeviceManager _deviceManager;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IConversationManager conversationManager,
        IChatAppService chatAppService,
        IChatNotifier chatNotifier,
        IDeviceManager deviceManager,
        ILogger<ChatHub> logger)
    {
        _conversationManager = conversationManager;
        _chatAppService = chatAppService;
        _chatNotifier = chatNotifier;
        _deviceManager = deviceManager;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            var identityId = await ResolveIdentityIdAsync();
            if (identityId == null)
            {
                _logger.LogWarning("连接 {ConnectionId} 认证失败，已中断", Context.ConnectionId);
                Context.Abort();
                return;
            }

            _logger.LogInformation("连接 {ConnectionId} 认证成功，IdentityId: {IdentityId}，IsDevice: {IsDevice}",
                Context.ConnectionId, identityId, IsDeviceConnection());

            var conversations = await _conversationManager.GetUserConversationsAsync(identityId.Value);
            foreach (var conversation in conversations)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"conv_{conversation.Id}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接 {ConnectionId} OnConnectedAsync 异常", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// 发送消息到会话
    /// </summary>
    public async Task<ChatMessageDto> SendMessage(Guid conversationId, string content, string messageType = "Text")
    {
        var senderId = await GetIdentityIdOrThrowAsync();
        await _conversationManager.EnsureParticipantAsync(conversationId, senderId);

        // 发消息前恢复已删除的参与者（对方删除会话后再发消息，会话自动恢复）
        await _conversationManager.ResurrectParticipantsAsync(conversationId);

        if (IsDeviceConnection())
        {
            var message = await _conversationManager.SendMessageAsync(conversationId, senderId, content, messageType);
            var deviceName = Context.GetHttpContext()?.Request.Query["deviceName"].ToString() ?? "设备";

            var messageDto = new ChatMessageDto
            {
                Id = message.Id,
                ConversationId = message.ConversationId,
                SenderId = message.SenderId,
                SenderName = deviceName,
                Content = message.Content,
                MessageType = message.MessageType,
                SentAt = message.SentAt,
                IsMine = false  // 广播给所有人，接收者自行判断
            };

            await _chatNotifier.NotifyMessageAsync(conversationId, messageDto, senderId);

            // 通知其他参与者未读计数更新 + 新会话通知（确保尚未加入 SignalR 组的接收方能加入并收到后续消息）
            var otherParticipants = await _conversationManager.GetParticipantsAsync(conversationId);
            foreach (var participant in otherParticipants.Where(p => p.UserId != senderId))
            {
                await _chatNotifier.NotifyUnreadCountUpdatedAsync(conversationId, participant.UserId, participant.UnreadCount);
                await _chatNotifier.NotifyNewConversationAsync(conversationId, participant.UserId);
            }

            return messageDto;
        }

        return await _chatAppService.SendMessageAsync(conversationId,
            new SendMessageDto { Content = content, MessageType = messageType });
    }

    /// <summary>
    /// 标记会话已读
    /// </summary>
    public async Task MarkAsRead(Guid conversationId)
    {
        var identityId = await GetIdentityIdOrThrowAsync();

        if (IsDeviceConnection())
        {
            await _conversationManager.MarkAsReadAsync(conversationId, identityId);
            await _chatNotifier.NotifyUnreadCountUpdatedAsync(conversationId, identityId, 0);
            return;
        }

        await _chatAppService.MarkAsReadAsync(conversationId);
    }

    /// <summary>
    /// 加入会话组（新创建的会话需要动态加入）
    /// </summary>
    public async Task JoinConversation(Guid conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"conv_{conversationId}");
    }

    /// <summary>
    /// 判断当前连接是否为设备认证
    /// </summary>
    private bool IsDeviceConnection()
    {
        return Context.User?.Identity?.IsAuthenticated != true;
    }

    /// <summary>
    /// 解析连接身份 ID：已认证用户读 sub claim，未认证的设备读 query 参数验证授权码。
    /// 返回 null 表示认证失败。
    /// </summary>
    private async Task<Guid?> ResolveIdentityIdAsync()
    {
        // 用户 JWT 认证
        if (Context.User?.Identity?.IsAuthenticated == true)
        {
            var userIdClaim = Context.User.FindFirst("sub") ??
                              Context.User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
                return userId;
        }

        // 设备授权码认证
        var httpContext = Context.GetHttpContext();
        var authorizationCode = httpContext?.Request.Query["authorizationCode"].ToString();
        _logger.LogInformation("设备连接尝试: ConnectionId={ConnectionId}, authorizationCode={Code}",
            Context.ConnectionId, authorizationCode ?? "(空)");

        if (string.IsNullOrEmpty(authorizationCode))
            return null;

        var device = await _deviceManager.GetDeviceByAuthorizationCodeAsync(authorizationCode);
        if (device == null)
        {
            _logger.LogWarning("授权码无效: {Code}", authorizationCode);
            return null;
        }

        // 同步设备名称和类型：如果连接时传的 deviceName/deviceType 与服务端不一致，更新服务端
        var deviceNameParam = httpContext?.Request.Query["deviceName"].ToString();
        var deviceTypeParam = httpContext?.Request.Query["deviceType"].ToString();

        if (!string.IsNullOrEmpty(deviceNameParam) || !string.IsNullOrEmpty(deviceTypeParam))
        {
            await _deviceManager.UpdateDeviceAsync(device.Id, deviceNameParam, deviceTypeParam, device.UserId);
        }

        _logger.LogInformation("设备认证成功: DeviceId={DeviceId}, DeviceName={DeviceName}, DeviceType={DeviceType}, Id={Id}",
            device.DeviceId, device.DeviceName, device.DeviceType, device.Id);

        return device.Id;
    }

    private async Task<Guid> GetIdentityIdOrThrowAsync()
    {
        var identityId = await ResolveIdentityIdAsync();
        if (identityId == null)
            throw new HubException("未授权");
        return identityId.Value;
    }
}
