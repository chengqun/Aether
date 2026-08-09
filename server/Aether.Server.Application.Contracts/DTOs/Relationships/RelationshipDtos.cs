using System;
using System.Collections.Generic;
using Aether.Server.Domain.Shared.Enums;

namespace Aether.Server.Application.Contracts.DTOs.Relationships
{
    /// <summary>
    /// 关系DTO
    /// </summary>
    public class RelationshipDto
    {
        public Guid Id { get; set; }
        public RelationshipType Type { get; set; }
        public string TargetId { get; set; }
        public string TargetName { get; set; }
        public RelationshipStatus Status { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime? LastModificationTime { get; set; }
        public string? AuthorizationCode { get; set; }
        public string? DeviceType { get; set; }
    }

    /// <summary>
    /// 通讯录数据DTO - 包含用户所有类型的联系人
    /// </summary>
    public class ContactBookDto
    {
        public List<RelationshipDto> Devices { get; set; } = new List<RelationshipDto>();
        public List<RelationshipDto> Friends { get; set; } = new List<RelationshipDto>();
        public List<RelationshipDto> Groups { get; set; } = new List<RelationshipDto>();
        public List<RelationshipDto> Stocks { get; set; } = new List<RelationshipDto>();
        public List<RelationshipDto> ServiceAccounts { get; set; } = new List<RelationshipDto>();
    }

    /// <summary>
    /// 目标对象DTO
    /// </summary>
    public class TargetDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public TargetSecurityInfo? SecurityInfo { get; set; }
        public TargetServiceAccountInfo? ServiceAccountInfo { get; set; }
    }

    /// <summary>
    /// 搜索结果中的股票详细信息
    /// </summary>
    public class TargetSecurityInfo
    {
        public string Code { get; set; } = string.Empty;
        public string SecurityType { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string BaseCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// 搜索结果中的服务号详细信息
    /// </summary>
    public class TargetServiceAccountInfo
    {
        public string Category { get; set; } = string.Empty;
        public bool IsVerified { get; set; }
    }
}