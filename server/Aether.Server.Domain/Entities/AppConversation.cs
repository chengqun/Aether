using System;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

public class AppConversation : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public ConversationType Type { get; set; }

    /// <summary>
    /// 单聊：两个 UserId 排序后用 | 连接（如 "guid1|guid2"）；群聊：GroupId 字符串
    /// </summary>
    public string ConversationKey { get; set; } = string.Empty;

    /// <summary>
    /// 群聊关联的 GroupId，单聊为 null
    /// </summary>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// 群聊显示名称，单聊为 null（查询时动态解析）
    /// </summary>
    public string? Name { get; set; }

    public AppConversation() { }

    public AppConversation(Guid id, ConversationType type, string conversationKey,
        Guid? groupId = null, string? name = null)
    {
        Id = id;
        Type = type;
        ConversationKey = conversationKey;
        GroupId = groupId;
        Name = name;
    }
}
