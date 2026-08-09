using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.Services.Devices;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Devices;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Aether.Server.Domain.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Aether.Server.Application.Services.Devices
{
    [Route("api/app/device")]
    public class DeviceAppService : ApplicationService, IDeviceAppService
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly ILogger<DeviceAppService> _logger;

        public DeviceAppService(IDeviceManager deviceManager, IRepository<AppRelationship, Guid> relationshipRepository, ILogger<DeviceAppService> logger)
        {
            _deviceManager = deviceManager;
            _relationshipRepository = relationshipRepository;
            _logger = logger;
        }

        [HttpPost("device")]
        public async Task<DeviceDto> CreateDeviceAsync(CreateDeviceDto input)
        {
            _logger.LogInformation("[DeviceAppService] CreateDeviceAsync called. UserId: {UserId}, DeviceName: {DeviceName}",
                CurrentUser.Id, input?.DeviceName);

            try
            {
                if (!CurrentUser.IsAuthenticated)
                {
                    _logger.LogWarning("[DeviceAppService] User not authenticated");
                    throw new UserFriendlyException("未登录");
                }

                var userId = CurrentUser.Id!.Value;
                _logger.LogInformation("[DeviceAppService] Creating device for user {UserId}", userId);

                var device = await _deviceManager.CreateDeviceAsync(
                    Guid.NewGuid().ToString("N"),
                    input.DeviceName,
                    input.DeviceType,
                    userId
                );

                _logger.LogInformation("[DeviceAppService] Device created successfully. DeviceId: {DeviceId}", device?.Id);

                return new DeviceDto
                {
                    Id = device.Id,
                    DeviceId = device.DeviceId,
                    DeviceName = device.DeviceName,
                    DeviceType = device.DeviceType,
                    AuthorizationCode = device.AuthorizationCode
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DeviceAppService] Error creating device: {Message}", ex.Message);
                throw;
            }
        }

        [HttpGet("device/{id}")]
        public async Task<DeviceDto> GetDeviceByIdAsync(Guid id)
        {
            if (!CurrentUser.IsAuthenticated)
                throw new UserFriendlyException("未登录");

            var userId = CurrentUser.Id!.Value;
            var device = await _deviceManager.GetDeviceByIdAsync(id, userId);

            if (device == null)
                throw new UserFriendlyException("设备不存在");

            return new DeviceDto
            {
                Id = device.Id,
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                DeviceType = device.DeviceType,
                AuthorizationCode = device.AuthorizationCode
            };
        }

        [HttpPost("generate-authorization-code/{deviceId}")]
        public async Task<string> GenerateAuthorizationCodeAsync(Guid deviceId)
        {
            if (!CurrentUser.IsAuthenticated)
                throw new UserFriendlyException("未登录");

            var userId = CurrentUser.Id!.Value;
            var device = await _deviceManager.GetDeviceByIdAsync(deviceId, userId);

            if (device == null)
                throw new UserFriendlyException("设备不存在");

            return await _deviceManager.GenerateAuthorizationCodeAsync(device.DeviceId, userId);
        }

        [HttpGet("device-by-code")]
        public async Task<DeviceDto> GetDeviceByAuthorizationCodeAsync(string authorizationCode)
        {
            var device = await _deviceManager.GetDeviceByAuthorizationCodeAsync(authorizationCode);

            if (device == null)
                throw new UserFriendlyException("授权码无效");

            return new DeviceDto
            {
                Id = device.Id,
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                DeviceType = device.DeviceType,
                AuthorizationCode = device.AuthorizationCode
            };
        }

        [HttpGet("my-devices")]
        public async Task<List<DeviceDto>> GetMyDevicesAsync()
        {
            if (!CurrentUser.IsAuthenticated)
                throw new UserFriendlyException("未登录");

            var userId = CurrentUser.Id!.Value;
            var devices = await _deviceManager.GetUserDevicesAsync(userId);

            // 查询当前用户所有已接受的设备关系
            var relationships = await _relationshipRepository.GetListAsync(
                r => r.UserId == userId && r.TargetType == RelationshipType.Device && r.Status == RelationshipStatus.Accepted
            );
            var relatedDeviceIds = relationships.Select(r => r.TargetId).ToHashSet();

            return devices.Select(d => new DeviceDto
            {
                Id = d.Id,
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                DeviceType = d.DeviceType,
                AuthorizationCode = d.AuthorizationCode,
                HasRelationship = relatedDeviceIds.Contains(d.DeviceId)
            }).ToList();
        }

        /// <summary>
        /// 测试用：创建测试设备并返回授权码（无需认证）
        /// </summary>
        [HttpPost("test-device")]
        public async Task<DeviceDto> CreateTestDeviceAsync([FromBody] CreateDeviceDto input)
        {
            _logger.LogInformation("[DeviceAppService] CreateTestDeviceAsync called. DeviceName: {DeviceName}", input?.DeviceName);

            try
            {
                // 使用固定的测试用户ID
                var testUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
                
                var device = await _deviceManager.CreateDeviceAsync(
                    Guid.NewGuid().ToString("N"),
                    input.DeviceName ?? "测试设备",
                    input.DeviceType ?? "Console",
                    testUserId
                );

                _logger.LogInformation("[DeviceAppService] Test device created successfully. AuthorizationCode: {AuthorizationCode}", device.AuthorizationCode);

                return new DeviceDto
                {
                    Id = device.Id,
                    DeviceId = device.DeviceId,
                    DeviceName = device.DeviceName,
                    DeviceType = device.DeviceType,
                    AuthorizationCode = device.AuthorizationCode
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DeviceAppService] Error creating test device: {Message}", ex.Message);
                throw;
            }
        }
    }
}