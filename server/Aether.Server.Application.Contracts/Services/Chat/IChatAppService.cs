using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Chat;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Chat;

public interface IChatAppService : IApplicationService
{
    Task<ConversationDto> GetOrCreateDirectConversationAsync(Guid targetUserId);
    Task<ConversationDto> GetOrCreateGroupConversationAsync(Guid groupId);
    Task<List<ConversationDto>> GetMyConversationsAsync();
    Task<List<ChatMessageDto>> GetMessagesAsync(Guid conversationId, int skipCount = 0, int maxResultCount = 50);
    Task<List<ChatMessageDto>> GetUnreadMessagesWithMarkAsync(Guid conversationId);
    Task<ChatMessageDto> SendMessageAsync(Guid conversationId, SendMessageDto input);
    Task MarkAsReadAsync(Guid conversationId);
    Task DeleteConversationAsync(Guid conversationId);
    Task<int> GetUnreadCountAsync();
}
