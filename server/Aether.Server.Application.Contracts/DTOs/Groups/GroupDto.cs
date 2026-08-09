using System;
using System.Collections.Generic;

namespace Aether.Server.Application.Contracts.DTOs.Groups
{
    public class GroupDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }
        public string OwnerUsername { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public DateTime CreationTime { get; set; }
    }

    public class GroupMemberDto
    {
        public Guid Id { get; set; }
        public Guid GroupId { get; set; }
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; }
    }

    public class CreateGroupDto
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>邀请加入的用户名列表（可选）</summary>
        public List<string> InviteUsernames { get; set; } = new();
    }

    public class AddGroupMemberDto
    {
        public string Username { get; set; } = string.Empty;
    }

    public class UpdateGroupMemberRoleDto
    {
        public string Role { get; set; } = string.Empty;
    }
}
