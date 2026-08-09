using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;

namespace Aether.Server.Domain.Services.Devices
{
    public interface IDeviceManager
    {
        Task<AppDevice> CreateDeviceAsync(string deviceId, string deviceName, string deviceType, Guid userId);
        Task<List<AppDevice>> GetUserDevicesAsync(Guid userId);
        Task<AppDevice> GetDeviceByIdAsync(Guid deviceId, Guid userId);
        Task<AppDevice> GetDeviceByDeviceIdAsync(string deviceId, Guid userId);
        Task UpdateDeviceAsync(Guid deviceId, string? deviceName, string? deviceType, Guid userId);
        Task DeleteDeviceAsync(Guid deviceId, Guid userId);
        Task<string> GenerateAuthorizationCodeAsync(string deviceId, Guid userId);
        Task<AppDevice> GetDeviceByAuthorizationCodeAsync(string authorizationCode);
    }
}