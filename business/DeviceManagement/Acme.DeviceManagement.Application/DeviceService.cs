using Acme.DeviceManagement.Domain;
using Acme.Platform.Domain;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.DeviceManagement.Application;

public class DeviceService : IDeviceService
{
    private readonly IAppServerHttpClient _httpClient;

    public DeviceService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MyDevice>> GetMyDevicesAsync()
    {
        return await _httpClient.GetAsync<List<MyDevice>>("api/app/device/my-devices") ?? new List<MyDevice>();
    }

    public async Task<MyDevice> CreateDeviceAsync(string deviceName, string deviceType)
    {
        var payload = new { DeviceName = deviceName, DeviceType = deviceType };
        var response = await _httpClient.PostAsync<MyDevice>("api/app/device/device", payload);
        return response ?? new MyDevice();
    }
}