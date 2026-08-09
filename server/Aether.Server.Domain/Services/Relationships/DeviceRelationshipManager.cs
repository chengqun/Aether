using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp;
using Volo.Abp.Identity;

namespace Aether.Server.Domain.Services.Relationships
{
    /// <summary>
    /// 设备关系管理器
    /// 使用AppRelationship统一关系表
    /// </summary>
    public class DeviceRelationshipManager : DomainService, IRelationshipManager<AppRelationship, AppDevice, Guid>
    {
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly IRepository<AppDevice, Guid> _deviceRepository;

        public RelationshipType Type => RelationshipType.Device;

        private readonly IRepository<IdentityUser, Guid> _userRepository;

        public DeviceRelationshipManager(
            IRepository<AppRelationship, Guid> relationshipRepository,
            IRepository<AppDevice, Guid> deviceRepository,
            IRepository<IdentityUser, Guid> userRepository)
        {
            _relationshipRepository = relationshipRepository;
            _deviceRepository = deviceRepository;
            _userRepository = userRepository;
        }

        public async Task<AppRelationship> CreateRelationshipAsync(Guid userId, string targetId)
        {
            string deviceId = targetId;

            // 检查设备是否存在
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device == null)
            {
                throw new BusinessException("Aether:DeviceNotFound");
            }

            // 检查是否已经存在关系
            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Device && r.TargetId == deviceId
            );

            if (existingRelationship != null)
            {
                throw new BusinessException("Aether:DeviceRelationshipExists");
            }

            // 自己的设备 → 自动 Accepted；别人的设备且未开放搜索 → 不允许添加
            if (device.UserId == userId)
            {
                var relationship = new AppRelationship(userId, RelationshipType.Device, deviceId, RelationshipStatus.Accepted);
                return await _relationshipRepository.InsertAsync(relationship);
            }

            if (!device.IsSearchable)
            {
                throw new BusinessException("Aether:DeviceNotSearchable");
            }

            // 别人的设备且已开放搜索 → 请求关系，等待设备所有者同意
            var pendingRelationship = new AppRelationship(userId, RelationshipType.Device, deviceId, RelationshipStatus.Pending);
            return await _relationshipRepository.InsertAsync(pendingRelationship);
        }

        public async Task<AppRelationship> AcceptRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查设备是否存在
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == relationship.TargetId);
            if (device == null)
            {
                throw new BusinessException("Aether:DeviceNotFound");
            }
            
            // 只有设备所有者才能接受请求
            if (device.UserId != currentUserId)
            {
                throw new BusinessException("Aether:NotDeviceOwner");
            }
            
            relationship.Accept();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task<AppRelationship> RejectRelationshipAsync(Guid relationshipId, Guid currentUserId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查设备是否存在
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == relationship.TargetId);
            if (device == null)
            {
                throw new BusinessException("Aether:DeviceNotFound");
            }
            
            // 只有设备所有者才能拒绝请求
            if (device.UserId != currentUserId)
            {
                throw new BusinessException("Aether:NotDeviceOwner");
            }
            
            relationship.Reject();
            return await _relationshipRepository.UpdateAsync(relationship);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId)
        {
            await _relationshipRepository.DeleteAsync(relationshipId);
        }

        public async Task RemoveRelationshipAsync(Guid relationshipId, Guid userId)
        {
            var relationship = await _relationshipRepository.GetAsync(relationshipId);
            
            // 检查设备是否存在
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == relationship.TargetId);
            if (device == null)
            {
                throw new BusinessException("Aether:DeviceNotFound");
            }
            
            // 只有设备所有者或关系创建者才能删除关系
            if (device.UserId != userId && relationship.UserId != userId)
            {
                throw new BusinessException("Aether:NoPermission");
            }
            
            await _relationshipRepository.DeleteAsync(relationship);
        }

        public async Task<List<AppRelationship>> GetUserRelationshipsAsync(Guid userId)
        {
            return await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Device && r.Status == RelationshipStatus.Accepted
            );
        }

        public async Task<List<AppRelationship>> GetPendingRequestsAsync(Guid userId)
        {
            // 获取发给当前用户的设备关系请求（用户是设备的所有者）
            var myDevices = await _deviceRepository.GetListAsync(d => d.UserId == userId);
            var deviceIds = myDevices.Select(d => d.DeviceId).ToList();

            return await _relationshipRepository.GetListAsync(
                r => deviceIds.Contains(r.TargetId) && r.TargetType == RelationshipType.Device && r.Status == RelationshipStatus.Pending
            );
        }

        public async Task<List<AppDevice>> SearchTargetsAsync(string keyword)
        {
            return await _deviceRepository.GetListAsync(
                d => d.IsSearchable && (d.DeviceName.Contains(keyword) || d.DeviceId.Contains(keyword))
            );
        }

        public async Task<bool> RelationshipExistsAsync(Guid userId, string targetId)
        {
            return await _relationshipRepository.AnyAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Device && r.TargetId == targetId
            );
        }
    }
}