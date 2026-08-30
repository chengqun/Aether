using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;

namespace Aether.Server.Application.Services.Chat;

[Authorize]
[Route("api/app/chat")]
public class ChatAppService : ApplicationService, IChatAppService
{
    private readonly IConversationManager _conversationManager;
    private readonly IRepository<AppChatMessage, Guid> _messageRepository;
    private readonly IRepository<AppConversationParticipant, Guid> _participantRepository;
    private readonly IRepository<IdentityUser, Guid> _userRepository;
    private readonly IChatNotifier _chatNotifier;

    public ChatAppService(
        IConversationManager conversationManager,
        IRepository<AppChatMessage, Guid> messageRepository,
        IRepository<AppConversationParticipant, Guid> participantRepository,
        IRepository<IdentityUser, Guid> userRepository,
        IChatNotifier chatNotifier)
    {
        _conversationManager = conversationManager;
        _messageRepository = messageRepository;
        _participantRepository = participantRepository;
        _userRepository = userRepository;
        _chatNotifier = chatNotifier;
    }

    [HttpPost("get-or-create-direct-conversation/{targetUserId}")]
    public async Task<ConversationDto> GetOrCreateDirectConversationAsync(Guid targetUserId)
    {
        var userId = CurrentUser.Id!.Value;
        var conversation = await _conversationManager.GetOrCreateDirectConversationAsync(userId, targetUserId);
        var otherUser = await _userRepository.GetAsync(targetUserId);
        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversation.Id && p.UserId == userId);
        var lastMessage = await GetLastMessageAsync(conversation.Id);
        var allParticipants = await _participantRepository.GetListAsync(p => p.ConversationId == conversation.Id);
        var memberCount = allParticipants.Count;

        return new ConversationDto
        {
            Id = conversation.Id,
            Type = (int)conversation.Type,
            OtherUserName = otherUser.UserName,
            OtherUserId = targetUserId,
            UnreadCount = participant?.UnreadCount ?? 0,
            LastMessage = lastMessage?.Content,
            LastMessageTime = lastMessage?.SentAt,
            MemberCount = memberCount
        };
    }

    [HttpPost("get-or-create-group-conversation/{groupId}")]
    public async Task<ConversationDto> GetOrCreateGroupConversationAsync(Guid groupId)
    {
        var userId = CurrentUser.Id!.Value;

        var groupRepo = LazyServiceProvider.LazyGetRequiredService<IRepository<AppGroup, Guid>>();
        var group = await groupRepo.GetAsync(groupId);

        var conversation = await _conversationManager.CreateGroupConversationAsync(groupId, group.Name);

        await _conversationManager.AddParticipantAsync(conversation.Id, userId);

        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversation.Id && p.UserId == userId);
        var lastMessage = await GetLastMessageAsync(conversation.Id);
        var allParticipants = await _participantRepository.GetListAsync(p => p.ConversationId == conversation.Id);
        var memberCount = allParticipants.Count;

        return new ConversationDto
        {
            Id = conversation.Id,
            Type = (int)conversation.Type,
            Name = conversation.Name,
            UnreadCount = participant?.UnreadCount ?? 0,
            LastMessage = lastMessage?.Content,
            LastMessageTime = lastMessage?.SentAt,
            MemberCount = memberCount
        };
    }

    [HttpPost("get-or-create-device-conversation/{deviceId}")]
    public async Task<ConversationDto> GetOrCreateDeviceConversationAsync([FromRoute] string deviceId)
    {
        var userId = CurrentUser.Id!.Value;

        var deviceRepo = LazyServiceProvider.LazyGetRequiredService<IRepository<AppDevice, Guid>>();

        AppDevice? device = null;

        // 按 DeviceId（string，32位无连线）查找
        device = await deviceRepo.FirstOrDefaultAsync(d => d.DeviceId == deviceId);

        // 按 Id（Guid 主键）查找
        if (device == null && Guid.TryParse(deviceId, out var guidId))
        {
            device = await deviceRepo.FirstOrDefaultAsync(d => d.Id == guidId);
        }

        // 按 DeviceId 去掉连线的格式查找
        if (device == null && Guid.TryParse(deviceId, out _))
        {
            var normalized = deviceId.Replace("-", "").ToLowerInvariant();
            device = await deviceRepo.FirstOrDefaultAsync(d => d.DeviceId == normalized);
        }

        if (device == null)
            throw new BusinessException("Aether:DeviceNotFound", $"设备不存在: {deviceId}");

        var conversation = await _conversationManager.GetOrCreateDeviceConversationAsync(userId, device.Id);

        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversation.Id && p.UserId == userId);
        var lastMessage = await GetLastMessageAsync(conversation.Id);
        var allParticipants = await _participantRepository.GetListAsync(p => p.ConversationId == conversation.Id);
        var memberCount = allParticipants.Count;

        return new ConversationDto
        {
            Id = conversation.Id,
            Type = (int)conversation.Type,
            Name = device.DeviceName,
            OtherUserName = device.DeviceName,
            DeviceType = device.DeviceType,
            UnreadCount = participant?.UnreadCount ?? 0,
            LastMessage = lastMessage?.Content,
            LastMessageTime = lastMessage?.SentAt,
            MemberCount = memberCount,
            OtherDeviceId = device.DeviceId
        };
    }

    [HttpGet("my-conversations")]
    public async Task<List<ConversationDto>> GetMyConversationsAsync()
    {
        var userId = CurrentUser.Id!.Value;
        var conversations = await _conversationManager.GetUserConversationsAsync(userId);
        var dtos = new List<ConversationDto>();

        foreach (var conv in conversations)
        {
            var participant = await _participantRepository.FirstOrDefaultAsync(p =>
                p.ConversationId == conv.Id && p.UserId == userId);
            var lastMessage = await GetLastMessageAsync(conv.Id);
            var allParticipants = await _participantRepository.GetListAsync(p => p.ConversationId == conv.Id);
            var memberCount = allParticipants.Count;

            var dto = new ConversationDto
            {
                Id = conv.Id,
                Type = (int)conv.Type,
                Name = conv.Name,
                UnreadCount = participant?.UnreadCount ?? 0,
                LastMessage = lastMessage?.Content,
                LastMessageTime = lastMessage?.SentAt,
                MemberCount = memberCount
            };

            if (conv.Type == Domain.Shared.Enums.ConversationType.Direct)
            {
                var otherParticipant = await _participantRepository.FirstOrDefaultAsync(p =>
                    p.ConversationId == conv.Id && p.UserId != userId);
                if (otherParticipant != null)
                {
                    var otherUser = await _userRepository.FirstOrDefaultAsync(u => u.Id == otherParticipant.UserId);
                    if (otherUser != null)
                    {
                        dto.OtherUserName = otherUser.UserName;
                    }
                    else
                    {
                        var deviceRepo = LazyServiceProvider.LazyGetRequiredService<IRepository<AppDevice, Guid>>();
                        var otherDevice = await deviceRepo.FirstOrDefaultAsync(d => d.Id == otherParticipant.UserId);
                        dto.OtherUserName = otherDevice?.DeviceName ?? "未知用户";
                        dto.DeviceType = otherDevice?.DeviceType;
                        dto.OtherDeviceId = otherDevice?.DeviceId;
                    }
                    dto.OtherUserId = otherParticipant.UserId;
                }
            }

            dtos.Add(dto);
        }

        return dtos.OrderByDescending(d => d.LastMessageTime ?? DateTime.MinValue).ToList();
    }

    [HttpGet("messages/{conversationId}")]
    public async Task<List<ChatMessageDto>> GetMessagesAsync(Guid conversationId, int skipCount = 0,
        int maxResultCount = 50)
    {
        var userId = CurrentUser.Id!.Value;
        await _conversationManager.EnsureParticipantAsync(conversationId, userId);

        var messages = await _messageRepository.GetListAsync(m => m.ConversationId == conversationId);
        var ordered = messages.OrderByDescending(m => m.SentAt).Skip(skipCount).Take(maxResultCount).ToList();

        // 批量解析发送者名（修 N+1：一次查用户表 + 一次查设备表，替代逐条 await）
        var nameMap = await ResolveNamesBatchAsync(ordered.Select(m => m.SenderId));

        var dtos = new List<ChatMessageDto>();
        foreach (var msg in ordered)
        {
            nameMap.TryGetValue(msg.SenderId, out var senderName);
            if (string.IsNullOrEmpty(senderName)) senderName = "未知用户";
            dtos.Add(new ChatMessageDto
            {
                Id = msg.Id,
                ConversationId = msg.ConversationId,
                SenderId = msg.SenderId,
                SenderName = senderName,
                Content = msg.Content,
                MessageType = msg.MessageType,
                SentAt = msg.SentAt,
                IsMine = msg.SenderId == userId
            });
        }

        return dtos.OrderBy(d => d.SentAt).ToList();
    }

    [HttpGet("unread-messages/{conversationId}")]
    public async Task<List<ChatMessageDto>> GetUnreadMessagesWithMarkAsync(Guid conversationId)
    {
        var userId = CurrentUser.Id!.Value;
        await _conversationManager.EnsureParticipantAsync(conversationId, userId);

        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversationId && p.UserId == userId);

        DateTime? lastReadAt = participant?.LastReadAt;

        var messages = await _messageRepository.GetListAsync(m => m.ConversationId == conversationId);
        var unreadMessages = lastReadAt.HasValue
            ? messages.Where(m => m.SentAt > lastReadAt.Value).ToList()
            : messages.ToList();

        await _conversationManager.MarkAsReadAsync(conversationId, userId);
        await _chatNotifier.NotifyUnreadCountUpdatedAsync(conversationId, userId, 0);

        var sortedUnread = unreadMessages.OrderBy(m => m.SentAt).ToList();
        var nameMap = await ResolveNamesBatchAsync(sortedUnread.Select(m => m.SenderId));

        var dtos = new List<ChatMessageDto>();
        foreach (var msg in sortedUnread)
        {
            nameMap.TryGetValue(msg.SenderId, out var senderName);
            if (string.IsNullOrEmpty(senderName)) senderName = "未知用户";
            dtos.Add(new ChatMessageDto
            {
                Id = msg.Id,
                ConversationId = msg.ConversationId,
                SenderId = msg.SenderId,
                SenderName = senderName,
                Content = msg.Content,
                MessageType = msg.MessageType,
                SentAt = msg.SentAt,
                IsMine = msg.SenderId == userId
            });
        }

        return dtos;
    }

    [HttpPost("send-message/{conversationId}")]
    public async Task<ChatMessageDto> SendMessageAsync(Guid conversationId, SendMessageDto input)
    {
        var userId = CurrentUser.Id!.Value;
        await _conversationManager.EnsureParticipantAsync(conversationId, userId);

        // 发消息前恢复已删除的参与者（对方删除会话后再发消息，会话自动恢复）
        await _conversationManager.ResurrectParticipantsAsync(conversationId);

        var message = await _conversationManager.SendMessageAsync(conversationId, userId, input.Content, input.MessageType);
        var senderName = await ResolveNameAsync(userId);

        var messageDto = new ChatMessageDto
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            SenderName = senderName,
            Content = message.Content,
            MessageType = message.MessageType,
            SentAt = message.SentAt,
            IsMine = true
        };

        // 广播给会话组：接收者应根据 SenderId 自行判断 IsMine
        var broadcastDto = new ChatMessageDto
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            SenderName = senderName,
            Content = message.Content,
            MessageType = message.MessageType,
            SentAt = message.SentAt,
            IsMine = false
        };

        await _chatNotifier.NotifyMessageAsync(conversationId, broadcastDto, userId);

        // 获取其他参与者并通知他们未读计数更新 + 新会话通知（确保尚未加入 SignalR 组的接收方能加入并收到后续消息）
        var otherParticipants = await _participantRepository.GetListAsync(p =>
            p.ConversationId == conversationId && p.UserId != userId);
        foreach (var participant in otherParticipants)
        {
            await _chatNotifier.NotifyUnreadCountUpdatedAsync(conversationId, participant.UserId, participant.UnreadCount);
            await _chatNotifier.NotifyNewConversationAsync(conversationId, participant.UserId);
        }

        return messageDto;
    }

    [HttpPost("mark-as-read/{conversationId}")]
    public async Task MarkAsReadAsync(Guid conversationId)
    {
        var userId = CurrentUser.Id!.Value;
        await _conversationManager.MarkAsReadAsync(conversationId, userId);
        await _chatNotifier.NotifyUnreadCountUpdatedAsync(conversationId, userId, 0);
    }

    [HttpDelete("delete-conversation/{conversationId}")]
    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var userId = CurrentUser.Id!.Value;
        await _conversationManager.RemoveParticipantAsync(conversationId, userId);
    }

    [HttpGet("unread-count")]
    public async Task<int> GetUnreadCountAsync()
    {
        var userId = CurrentUser.Id!.Value;
        return await _conversationManager.GetTotalUnreadCountAsync(userId);
    }

    private async Task<AppChatMessage?> GetLastMessageAsync(Guid conversationId)
    {
        var messages = await _messageRepository.GetListAsync(m => m.ConversationId == conversationId);
        return messages.OrderByDescending(m => m.SentAt).FirstOrDefault();
    }

    private async Task<string> ResolveNameAsync(Guid identityId)
    {
        var user = await _userRepository.FirstOrDefaultAsync(u => u.Id == identityId);
        if (user != null)
            return user.UserName ?? "未知用户";

        var deviceRepo = LazyServiceProvider.LazyGetRequiredService<IRepository<AppDevice, Guid>>();
        var device = await deviceRepo.FirstOrDefaultAsync(d => d.Id == identityId);
        if (device != null)
            return device.DeviceName;

        return "未知用户";
    }

    /// <summary>
    /// 批量解析发送者名：一次性查用户表 + 设备表（各一次 DB 往返），替代逐条 <see cref="ResolveNameAsync"/> 的 N+1。
    /// 设备消息 SenderId = AppDevice.Id（非 DeviceId 字符串），故按 Id 匹配；未命中用户再查设备。
    /// </summary>
    private async Task<Dictionary<Guid, string>> ResolveNamesBatchAsync(IEnumerable<Guid> identityIds)
    {
        var distinct = identityIds.Distinct().ToList();
        var nameMap = new Dictionary<Guid, string>();

        // 1. 批量查用户（EF 翻译为 IN 子句）
        var users = await _userRepository.GetListAsync(u => distinct.Contains(u.Id));
        foreach (var u in users)
            nameMap[u.Id] = u.UserName ?? "未知用户";

        // 2. 剩余未命中用户的 id 批量查设备（设备消息发送者在用户表查不到）
        var unresolved = distinct.Where(id => !nameMap.ContainsKey(id)).ToList();
        if (unresolved.Count > 0)
        {
            var deviceRepo = LazyServiceProvider.LazyGetRequiredService<IRepository<AppDevice, Guid>>();
            var devices = await deviceRepo.GetListAsync(d => unresolved.Contains(d.Id));
            foreach (var d in devices)
                nameMap[d.Id] = d.DeviceName ?? "设备";
        }

        return nameMap;
    }
}
