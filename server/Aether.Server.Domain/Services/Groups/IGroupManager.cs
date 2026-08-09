using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;

namespace Aether.Server.Domain.Services.Groups
{
    public interface IGroupManager
    {
        Task<AppGroup> CreateGroupAsync(string name, Guid ownerId, string description = "");
        Task<AppGroup> GetGroupAsync(Guid groupId);
        Task<List<AppGroup>> SearchGroupsAsync(string keyword);
        Task DisbandGroupAsync(Guid groupId);
    }
}