using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;

namespace Aether.Server.Domain.Services.Chat;

public interface IConversationManager
{
    Task<AppConversation> GetOrCreateDirectConversationAsync(Guid user1Id, Guid user2Id);
    Task<AppConversation> GetOrCreateDeviceConversationAsync(Guid userId, Guid deviceId);
    Task<AppConversation> CreateGroupConversationAsync(Guid groupId, string groupName);
    Task<AppConversation> GetConversationAsync(Guid conversationId);
    Task<List<AppConversation>> GetUserConversationsAsync(Guid userId);
    Task<AppChatMessage> SendMessageAsync(Guid conversationId, Guid senderId, string content, string messageType = "Text");
    Task MarkAsReadAsync(Guid conversationId, Guid userId);
    Task<int> GetTotalUnreadCountAsync(Guid userId);
    Task<AppConversationParticipant> AddParticipantAsync(Guid conversationId, Guid userId, string role = "Member");
    Task RemoveParticipantAsync(Guid conversationId, Guid userId);
    Task EnsureParticipantAsync(Guid conversationId, Guid userId);
    Task<List<AppConversationParticipant>> GetParticipantsAsync(Guid conversationId);
    Task ResurrectParticipantsAsync(Guid conversationId);
}
