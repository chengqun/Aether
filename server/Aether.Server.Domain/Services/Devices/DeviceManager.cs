using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Relationships;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Guids;
using Microsoft.Extensions.Logging;
using Volo.Abp;

namespace Aether.Server.Domain.Services.Devices
{
    public class DeviceManager : DomainService, IDeviceManager
    {
        private readonly IRepository<AppDevice, Guid> _deviceRepository;
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly ILogger<DeviceManager> _logger;

        public DeviceManager(
            IRepository<AppDevice, Guid> deviceRepository,
            IRepository<AppRelationship, Guid> relationshipRepository,
            ILogger<DeviceManager> logger)
        {
            _deviceRepository = deviceRepository;
            _relationshipRepository = relationshipRepository;
            _logger = logger;
        }

        public async Task<AppDevice> CreateDeviceAsync(string deviceId, string deviceName, string deviceType, Guid userId)
        {
            _logger.LogInformation("[DeviceManager] CreateDeviceAsync start. UserId: {UserId}, DeviceId: {DeviceId}, DeviceName: {DeviceName}",
                userId, deviceId, deviceName);

            var existingDevice = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.UserId == userId);
            if (existingDevice != null)
            {
                _logger.LogInformation("[DeviceManager] Device already exists: {DeviceId}", deviceId);
                return existingDevice;
            }

            var device = new AppDevice(
                GuidGenerator.Create(),
                deviceId,
                deviceName,
                deviceType,
                GuidGenerator.Create().ToString("N"),
                userId);

            _logger.LogInformation("[DeviceManager] Inserting device to repository...");
            await _deviceRepository.InsertAsync(device);
            _logger.LogInformation("[DeviceManager] Device inserted successfully. DeviceId: {DeviceId}", deviceId);

            // 直接创建关系，不通过DeviceRelationshipManager（避免同一UOW内查询不到刚插入的设备）
            _logger.LogInformation("[DeviceManager] Creating device relationship for user {UserId} device {DeviceId}", userId, deviceId);

            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Device && r.TargetId == deviceId
            );

            if (existingRelationship == null)
            {
                // 设备所有者自动建立 Accepted 关系
                var relationship = new AppRelationship(userId, RelationshipType.Device, deviceId, RelationshipStatus.Accepted);
                await _relationshipRepository.InsertAsync(relationship);
                _logger.LogInformation("[DeviceManager] Device relationship created successfully");
            }
            else
            {
                _logger.LogInformation("[DeviceManager] Device relationship already exists, ignoring...");
            }

            return device;
        }

        public async Task<List<AppDevice>> GetUserDevicesAsync(Guid userId)
        {
            var devices = await _deviceRepository.GetListAsync(d => d.UserId == userId);
            _logger.LogInformation("[DeviceManager] Retrieved {Count} devices for user {UserId}", devices.Count, userId);
            return devices;
        }

        public async Task<AppDevice> GetDeviceByIdAsync(Guid deviceId, Guid userId)
        {
            return await _deviceRepository.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId);
        }

        public async Task<AppDevice> GetDeviceByDeviceIdAsync(string deviceId, Guid userId)
        {
            return await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.UserId == userId);
        }

        public async Task UpdateDeviceAsync(Guid deviceId, string? deviceName, string? deviceType, Guid userId)
        {
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId);
            if (device != null)
            {
                var hasChanges = false;
                if (!string.IsNullOrEmpty(deviceName) && device.DeviceName != deviceName)
                {
                    device.Rename(deviceName);
                    hasChanges = true;
                    _logger.LogInformation("[DeviceManager] Updated device {DeviceId} name to '{DeviceName}'", deviceId, deviceName);
                }
                if (!string.IsNullOrEmpty(deviceType) && device.DeviceType != deviceType)
                {
                    device.ChangeDeviceType(deviceType);
                    hasChanges = true;
                    _logger.LogInformation("[DeviceManager] Updated device {DeviceId} type to '{DeviceType}'", deviceId, deviceType);
                }
                if (hasChanges)
                {
                    await _deviceRepository.UpdateAsync(device, autoSave: true);
                    _logger.LogInformation("[DeviceManager] Saved device changes to database");
                }
            }
        }

        public async Task DeleteDeviceAsync(Guid deviceId, Guid userId)
        {
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId);
            if (device != null)
            {
                await _deviceRepository.DeleteAsync(device);
                _logger.LogInformation("[DeviceManager] Deleted device {DeviceId}", deviceId);
            }
        }

        public async Task<string> GenerateAuthorizationCodeAsync(string deviceId, Guid userId)
        {
            var device = await _deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.UserId == userId);
            if (device == null)
            {
                throw new BusinessException("Aether:DeviceNotFound");
            }

            device.SetAuthorizationCode(GuidGenerator.Create().ToString("N"));
            await _deviceRepository.UpdateAsync(device);
            _logger.LogInformation("[DeviceManager] Generated authorization code for device {DeviceId}", deviceId);

            return device.AuthorizationCode;
        }

        public async Task<AppDevice> GetDeviceByAuthorizationCodeAsync(string authorizationCode)
        {
            return await _deviceRepository.FirstOrDefaultAsync(d => d.AuthorizationCode == authorizationCode);
        }
    }
}