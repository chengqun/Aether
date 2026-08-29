using System;
using System.Collections.Generic;
using System.Linq;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// Agent 信息展示项（AI 设置页股票模式 Agent 选择器用）。
/// 从 <c>CustomAgentDefinition</c> 映射而来，使 VM 不必在设置页暴露完整定义。
/// </summary>
public class AgentInfoDto
{
    /// <summary>Agent 稳定身份（存于 labs 文件 front-matter 的 id: 字段）。管理入口编辑/删除靠它定位。</summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentKind Kind { get; set; }

    /// <summary>类型展示标签（智能体/工作流/规则），供列表与表单展示。</summary>
    public string KindLabel => Kind switch
    {
        AgentKind.Workflow => "工作流",
        AgentKind.RuleBased => "规则",
        _ => "智能体",
    };
}

/// <summary>
/// 关系类型（值与 Aether <see cref="Relationship.Type"/> 的 int 一致）。
/// 移植自旧 AlphaAgent 的 RelationshipType 枚举。
/// </summary>
public enum RelationshipType
{
    Friendship = 0,
    Device = 1,
    Group = 2,
    Stock = 3,
    ServiceAccount = 4,
}

/// <summary>
/// 关系状态（值与 Aether <see cref="Relationship.Status"/> 的 int 一致）。
/// </summary>
public enum RelationshipStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>股票目标的附加信息（搜索结果展示用）。</summary>
public class TargetSecurityInfo
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SecurityType { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string BaseCode { get; set; } = string.Empty;
}

/// <summary>服务号目标的附加信息。</summary>
public class TargetServiceAccountInfo
{
    public string Category { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
}

/// <summary>
/// 通讯录条目展示模型（从 Aether <see cref="Relationship"/> 适配而来）。
/// </summary>
public class RelationshipDto
{
    public Guid Id { get; set; }
    public int Type { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? DeviceType { get; set; }

    /// <summary>名称首字符（用于头像占位）。</summary>
    public string Initial => string.IsNullOrEmpty(TargetName) ? "?" : TargetName[..1];
}

/// <summary>
/// 通讯录分组容器（按关系类型分组）。从 <see cref="Relationship"/> 列表构建。
/// </summary>
public class ContactBookDto
{
    public List<RelationshipDto> Devices { get; set; } = new();
    public List<RelationshipDto> Friends { get; set; } = new();
    public List<RelationshipDto> Groups { get; set; } = new();
    public List<RelationshipDto> Stocks { get; set; } = new();
    public List<RelationshipDto> ServiceAccounts { get; set; } = new();
}

/// <summary>视频条目展示模型（移植自旧 AlphaAgent VideoItemDto）。</summary>
public class VideoItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string Author { get; set; } = string.Empty;
    public int Duration { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>从 Aether <see cref="Relationship"/> 列表构建 <see cref="ContactBookDto"/>。</summary>
public static class ContactBookBuilder
{
    public static ContactBookDto Build(IEnumerable<Relationship> relationships)
    {
        var book = new ContactBookDto();
        foreach (var r in relationships ?? Enumerable.Empty<Relationship>())
        {
            var dto = new RelationshipDto
            {
                Id = r.Id,
                Type = r.Type,
                TargetId = r.TargetId,
                TargetName = r.TargetName,
                Status = r.Status,
                DeviceType = r.DeviceType,
            };

            switch ((RelationshipType)r.Type)
            {
                case RelationshipType.Friendship:
                    book.Friends.Add(dto);
                    break;
                case RelationshipType.Device:
                    book.Devices.Add(dto);
                    break;
                case RelationshipType.Group:
                    book.Groups.Add(dto);
                    break;
                case RelationshipType.Stock:
                    book.Stocks.Add(dto);
                    break;
                case RelationshipType.ServiceAccount:
                    book.ServiceAccounts.Add(dto);
                    break;
            }
        }
        return book;
    }
}
