using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Chat;
using Aether.Server.Domain.Services.Devices;
using Aether.Server.HttpApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;

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
    private readonly IDeviceConnectionTracker _deviceConnectionTracker;
    private readonly IRepository<IdentityUser, Guid> _userRepository;
    private readonly IRepository<AppDevice, Guid> _deviceRepository;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IConversationManager conversationManager,
        IChatAppService chatAppService,
        IChatNotifier chatNotifier,
        IDeviceManager deviceManager,
        IDeviceConnectionTracker deviceConnectionTracker,
        IRepository<IdentityUser, Guid> userRepository,
        IRepository<AppDevice, Guid> deviceRepository,
        ILogger<ChatHub> logger)
    {
        _conversationManager = conversationManager;
        _chatAppService = chatAppService;
        _chatNotifier = chatNotifier;
        _deviceManager = deviceManager;
        _deviceConnectionTracker = deviceConnectionTracker;
        _userRepository = userRepository;
        _deviceRepository = deviceRepository;
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

            // 设备连接登记到追踪器：设备无 sub claim，Clients.User 触达不了，
            // 需追踪器让服务端能主动把已连接设备拉进新建会话的 SignalR 组。
            if (IsDeviceConnection())
                _deviceConnectionTracker.Register(identityId.Value, Context.ConnectionId);

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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // 连接断开注销追踪（按 connectionId 反查 deviceId；非设备连接或未登记则无操作）。
        _deviceConnectionTracker.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
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
    /// 设备上报当前选中的专家/目标显示名（供手机只读状态显示）。仅设备连接可用。
    /// 桌面端在目标切换 +（重）连接成功后调用，让手机能看「桌面当前会用哪个专家回」。
    /// </summary>
    public async Task ReportSelectedAgent(string? agentName)
    {
        var deviceId = await GetIdentityIdOrThrowAsync();
        if (!IsDeviceConnection()) return;
        _deviceConnectionTracker.SetSelectedAgent(deviceId, agentName);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 手机查询某台桌面电脑的在线状态 + 当前选中专家名（只读）。
    /// deviceId 参数是 32 位 DeviceId 字符串（手机端既有字段），服务端反查主键 + 校验归属。
    /// 返回 { IsOnline, SelectedAgentName }；设备从未上报过 SelectedAgentName 为空。
    /// </summary>
    public async Task<object?> GetDeviceStatus(string deviceId)
    {
        var userId = await GetIdentityIdOrThrowAsync();
        if (IsDeviceConnection()) return null;  // 仅用户连接可查（手机端 JWT）
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        var device = await _deviceManager.GetDeviceByDeviceIdAsync(deviceId, userId);
        if (device == null) return null;

        var isOnline = _deviceConnectionTracker.GetConnections(device.Id).Count > 0;
        var agentName = _deviceConnectionTracker.GetSelectedAgent(device.Id) ?? string.Empty;
        return new { IsOnline = isOnline, SelectedAgentName = agentName };
    }

    /// <summary>
    /// 设备补拉离线消息：返回该设备各会话中「最后一条未被设备回复的用户消息」（每会话至多一条）。
    /// 桌面端离线期间手机发的消息已持久化但实时广播丢失——重连/冷启动后调用此方法取回并回放，
    /// 避免老板「发了没反应」。仅设备连接可用。
    /// </summary>
    public async Task<List<ChatMessageDto>> GetCatchUpMessages()
    {
        var deviceId = await GetIdentityIdOrThrowAsync();
        if (!IsDeviceConnection())
            return new List<ChatMessageDto>();

        var messages = await _conversationManager.GetCatchUpMessagesAsync(deviceId);
        if (messages.Count == 0)
            return new List<ChatMessageDto>();

        var nameMap = await ResolveNamesBatchAsync(messages.Select(m => m.SenderId).Distinct());
        return messages.Select(m => new ChatMessageDto
        {
            Id = m.Id,
            ConversationId = m.ConversationId,
            SenderId = m.SenderId,
            SenderName = nameMap.TryGetValue(m.SenderId, out var n) ? n : "用户",
            Content = m.Content,
            MessageType = m.MessageType,
            SentAt = m.SentAt,
            IsMine = false
        }).ToList();
    }

    /// <summary>批量解析发送者名（用户表 + 设备表各一次查询）。镜像 ChatAppService.ResolveNamesBatchAsync。</summary>
    private async Task<Dictionary<Guid, string>> ResolveNamesBatchAsync(IEnumerable<Guid> identityIds)
    {
        var ids = identityIds.ToList();
        var map = new Dictionary<Guid, string>();
        var users = await _userRepository.GetListAsync(u => ids.Contains(u.Id));
        foreach (var u in users)
            map[u.Id] = u.UserName;
        var missing = ids.Except(map.Keys).ToList();
        if (missing.Count > 0)
        {
            var devices = await _deviceRepository.GetListAsync(d => missing.Contains(d.Id));
            foreach (var d in devices)
                map[d.Id] = d.DeviceName;
        }
        return map;
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
        // 授权码是登录凭证，不落明文日志（防日志泄漏 → 越权冒充设备）。仅记长度 + 首尾掩码。
        _logger.LogInformation("设备连接尝试: ConnectionId={ConnectionId}, authorizationCode={Code}",
            Context.ConnectionId, MaskAuthorizationCode(authorizationCode));

        if (string.IsNullOrEmpty(authorizationCode))
            return null;

        var device = await _deviceManager.GetDeviceByAuthorizationCodeAsync(authorizationCode);
        if (device == null)
        {
            _logger.LogWarning("授权码无效: {Code}", MaskAuthorizationCode(authorizationCode));
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

    /// <summary>授权码掩码：仅留长度 + 首尾各 4 字符，中间用 * 替代。空/过短直接记 "(空)" 不泄露。</summary>
    private static string MaskAuthorizationCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "(空)";
        if (code.Length <= 8) return new string('*', code.Length);
        return $"{code[..4]}***{code[^4..]}(len={code.Length})";
    }
}
