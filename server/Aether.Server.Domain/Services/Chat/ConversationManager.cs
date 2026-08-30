using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Guids;

namespace Aether.Server.Domain.Services.Chat;

public class ConversationManager : DomainService, IConversationManager
{
    private readonly IRepository<AppConversation, Guid> _conversationRepository;
    private readonly IRepository<AppConversationParticipant, Guid> _participantRepository;
    private readonly IRepository<AppChatMessage, Guid> _messageRepository;
    private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
    private readonly ILogger<ConversationManager> _logger;

    public ConversationManager(
        IRepository<AppConversation, Guid> conversationRepository,
        IRepository<AppConversationParticipant, Guid> participantRepository,
        IRepository<AppChatMessage, Guid> messageRepository,
        IRepository<AppRelationship, Guid> relationshipRepository,
        ILogger<ConversationManager> logger)
    {
        _conversationRepository = conversationRepository;
        _participantRepository = participantRepository;
        _messageRepository = messageRepository;
        _relationshipRepository = relationshipRepository;
        _logger = logger;
    }

    public async Task<AppConversation> GetOrCreateDirectConversationAsync(Guid user1Id, Guid user2Id)
    {
        var key = BuildDirectConversationKey(user1Id, user2Id);

        var existing = await _conversationRepository.FirstOrDefaultAsync(c => c.ConversationKey == key);
        if (existing != null)
        {
            // 检查用户是否仍是参与者（用户可能之前删除过会话）
            var user1ParticipantExists = await _participantRepository.AnyAsync(p =>
                p.ConversationId == existing.Id && p.UserId == user1Id);
            if (!user1ParticipantExists)
            {
                await _participantRepository.InsertAsync(
                    new AppConversationParticipant(GuidGenerator.Create(), existing.Id, user1Id), true);
            }

            var user2ParticipantExists = await _participantRepository.AnyAsync(p =>
                p.ConversationId == existing.Id && p.UserId == user2Id);
            if (!user2ParticipantExists)
            {
                await _participantRepository.InsertAsync(
                    new AppConversationParticipant(GuidGenerator.Create(), existing.Id, user2Id), true);
            }

            return existing;
        }

        var conversation = new AppConversation(
            GuidGenerator.Create(),
            ConversationType.Direct,
            key
        );

        await _conversationRepository.InsertAsync(conversation, true);

        await _participantRepository.InsertAsync(
            new AppConversationParticipant(GuidGenerator.Create(), conversation.Id, user1Id), true);
        await _participantRepository.InsertAsync(
            new AppConversationParticipant(GuidGenerator.Create(), conversation.Id, user2Id), true);

        _logger.LogInformation("Created direct conversation {ConvId} for {User1} and {User2}",
            conversation.Id, user1Id, user2Id);

        return conversation;
    }

    /// <summary>
    /// 创建或获取用户与设备的会话
    /// </summary>
    public async Task<AppConversation> GetOrCreateDeviceConversationAsync(Guid userId, Guid deviceId)
    {
        var key = $"device_{userId}_{deviceId}";

        var existing = await _conversationRepository.FirstOrDefaultAsync(c => c.ConversationKey == key);
        if (existing != null)
        {
            // 检查用户与设备是否仍是参与者（用户或设备可能之前删除过会话）。对称恢复二者——
            // 仅恢复用户会导致设备 participant 缺失 → GetUserConversationsAsync(deviceId) 取不到该会话
            // → 桌面设备连接 OnConnectedAsync 自动 join 漏掉该会话组 → 桌面收不到手机消息。
            var userParticipantExists = await _participantRepository.AnyAsync(p =>
                p.ConversationId == existing.Id && p.UserId == userId);
            if (!userParticipantExists)
            {
                await _participantRepository.InsertAsync(
                    new AppConversationParticipant(GuidGenerator.Create(), existing.Id, userId), true);
            }

            var deviceParticipantExists = await _participantRepository.AnyAsync(p =>
                p.ConversationId == existing.Id && p.UserId == deviceId);
            if (!deviceParticipantExists)
            {
                await _participantRepository.InsertAsync(
                    new AppConversationParticipant(GuidGenerator.Create(), existing.Id, deviceId), true);
            }

            return existing;
        }

        var conversation = new AppConversation(
            GuidGenerator.Create(),
            ConversationType.Direct,
            key,
            deviceId,
            null
        );

        await _conversationRepository.InsertAsync(conversation, true);

        await _participantRepository.InsertAsync(
            new AppConversationParticipant(GuidGenerator.Create(), conversation.Id, userId), true);
        await _participantRepository.InsertAsync(
            new AppConversationParticipant(GuidGenerator.Create(), conversation.Id, deviceId), true);

        _logger.LogInformation("Created device conversation {ConvId} for user {UserId} and device {DeviceId}",
            conversation.Id, userId, deviceId);

        return conversation;
    }

    public async Task<AppConversation> CreateGroupConversationAsync(Guid groupId, string groupName)
    {
        var key = groupId.ToString();

        var existing = await _conversationRepository.FirstOrDefaultAsync(c => c.ConversationKey == key);
        if (existing != null)
            return existing;

        var conversation = new AppConversation(
            GuidGenerator.Create(),
            ConversationType.Group,
            key,
            groupId,
            groupName
        );

        await _conversationRepository.InsertAsync(conversation, true);

        _logger.LogInformation("Created group conversation {ConvId} for group {GroupId}",
            conversation.Id, groupId);

        return conversation;
    }

    public async Task<AppConversation> GetConversationAsync(Guid conversationId)
    {
        return await _conversationRepository.GetAsync(conversationId);
    }

    public async Task<List<AppConversation>> GetUserConversationsAsync(Guid userId)
    {
        var participantRecords = await _participantRepository.GetListAsync(p => p.UserId == userId);
        var conversationIds = participantRecords.Select(p => p.ConversationId).ToList();
        return await _conversationRepository.GetListAsync(c => conversationIds.Contains(c.Id));
    }

    public async Task<AppChatMessage> SendMessageAsync(Guid conversationId, Guid senderId, string content,
        string messageType = "Text")
    {
        var message = new AppChatMessage(GuidGenerator.Create(), conversationId, senderId, content, messageType);
        await _messageRepository.InsertAsync(message, true);

        // 递增其他参与者的未读数
        var participants = await _participantRepository.GetListAsync(p =>
            p.ConversationId == conversationId && p.UserId != senderId);
        foreach (var participant in participants)
        {
            participant.IncrementUnread();
            await _participantRepository.UpdateAsync(participant);
        }

        return message;
    }

    public async Task MarkAsReadAsync(Guid conversationId, Guid userId)
    {
        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversationId && p.UserId == userId);
        if (participant != null)
        {
            participant.MarkAsRead();
            await _participantRepository.UpdateAsync(participant);
        }
    }

    public async Task<int> GetTotalUnreadCountAsync(Guid userId)
    {
        var participants = await _participantRepository.GetListAsync(p => p.UserId == userId);
        return participants.Sum(p => p.UnreadCount);
    }

    public async Task<AppConversationParticipant> AddParticipantAsync(Guid conversationId, Guid userId,
        string role = "Member")
    {
        var existing = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversationId && p.UserId == userId);
        if (existing != null)
            return existing;

        var participant = new AppConversationParticipant(GuidGenerator.Create(), conversationId, userId, role);
        await _participantRepository.InsertAsync(participant, true);
        return participant;
    }

    public async Task RemoveParticipantAsync(Guid conversationId, Guid userId)
    {
        var participant = await _participantRepository.FirstOrDefaultAsync(p =>
            p.ConversationId == conversationId && p.UserId == userId);
        if (participant != null)
            await _participantRepository.DeleteAsync(participant);
    }

    public async Task EnsureParticipantAsync(Guid conversationId, Guid userId)
    {
        var exists = await _participantRepository.AnyAsync(p =>
            p.ConversationId == conversationId && p.UserId == userId);
        if (!exists)
            throw new BusinessException("Aether:NotConversationParticipant");
    }

    public async Task<List<AppConversationParticipant>> GetParticipantsAsync(Guid conversationId)
    {
        return await _participantRepository.GetListAsync(p => p.ConversationId == conversationId);
    }

    /// <summary>
    /// 恢复已删除的参与者：发消息时调用，确保会话的所有合法参与者都在 participant 表中。
    /// Direct 会话：根据 ConversationKey 解析出所有 userId，重新添加缺失的。
    /// Group 会话：从 AppRelationship 查询群成员，自动添加为 participant。
    /// </summary>
    public async Task ResurrectParticipantsAsync(Guid conversationId)
    {
        var conversation = await _conversationRepository.GetAsync(conversationId);

        if (conversation.Type == ConversationType.Direct)
        {
            var key = conversation.ConversationKey;

            if (key.StartsWith("device_"))
            {
                // device_{userId}_{deviceId} — 恢复用户和设备 participant
                var parts = key.Split('_');
                if (parts.Length >= 3)
                {
                    if (Guid.TryParse(parts[1], out var userId))
                        await AddParticipantAsync(conversationId, userId);
                    if (Guid.TryParse(parts[2], out var deviceId))
                        await AddParticipantAsync(conversationId, deviceId);
                }
            }
            else
            {
                // guidA|guidB — 恢复两个用户 participant
                var ids = key.Split('|');
                if (ids.Length == 2)
                {
                    if (Guid.TryParse(ids[0], out var user1Id))
                        await AddParticipantAsync(conversationId, user1Id);
                    if (Guid.TryParse(ids[1], out var user2Id))
                        await AddParticipantAsync(conversationId, user2Id);
                }
            }
        }
        else if (conversation.Type == ConversationType.Group && conversation.GroupId.HasValue)
        {
            // 从 AppRelationship 查询该群的所有已接受成员，添加为 participant
            var groupIdStr = conversation.GroupId.Value.ToString();
            var groupMembers = await _relationshipRepository.GetListAsync(r =>
                r.TargetType == RelationshipType.Group &&
                r.TargetId == groupIdStr &&
                r.Status == RelationshipStatus.Accepted);

            foreach (var member in groupMembers)
            {
                await AddParticipantAsync(conversationId, member.UserId);
            }
        }
    }

    private static string BuildDirectConversationKey(Guid user1Id, Guid user2Id)
    {
        var ids = new[] { user1Id.ToString(), user2Id.ToString() };
        Array.Sort(ids, StringComparer.Ordinal);
        return $"{ids[0]}|{ids[1]}";
    }
}
