using System;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;

namespace Aether.Server.Domain.Services.Moment
{
    public interface IMomentManager
    {
        Task<AppMoment> CreateMomentAsync(Guid userId, string content, string imageUrl = null, string type = "User", string visibility = "Friends");
        Task<AppMoment> CreateStockMomentAsync(int stockId, string content, string imageUrl = null);
        Task<AppMoment> CreateStockMomentAsync(int stockId, string content, DateTime createdAt, string imageUrl = null);
        Task<AppMoment> CreateDeviceMomentAsync(string deviceId, string content, string imageUrl = null);
        Task<AppMoment> CreateGroupMomentAsync(string groupId, string content, string imageUrl = null);
        Task DeleteMomentAsync(Guid momentId, Guid currentUserId);
        Guid CreateStockGuid(int stockId);
        Guid CreateTargetGuid(string targetId);
    }
}
