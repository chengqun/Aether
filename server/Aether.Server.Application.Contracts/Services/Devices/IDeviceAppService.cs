using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Devices
{
    public interface IDeviceAppService : IApplicationService
    {
        Task<DeviceDto> CreateDeviceAsync(CreateDeviceDto input);
        Task<DeviceDto> GetDeviceByIdAsync(Guid id);
        Task<string> GenerateAuthorizationCodeAsync(Guid deviceId);
        Task<DeviceDto> GetDeviceByAuthorizationCodeAsync(string authorizationCode);
        Task<List<DeviceDto>> GetMyDevicesAsync();
    }

    public class CreateDeviceDto
    {
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
    }

    public class DeviceDto
    {
        public Guid Id { get; set; }
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public string AuthorizationCode { get; set; }
        public bool HasRelationship { get; set; }
    }
}