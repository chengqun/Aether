using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.DeviceManagement.Domain;

public interface IDeviceService
{
    Task<List<MyDevice>> GetMyDevicesAsync();
    Task<MyDevice> CreateDeviceAsync(string deviceName, string deviceType);
}