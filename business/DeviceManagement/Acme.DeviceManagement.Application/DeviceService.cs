using Acme.DeviceManagement.Domain;
using Acme.AppAuth.Domain;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.DeviceManagement.Application;

public class DeviceService : IDeviceService
{
    private readonly IAppServerHttpClient _httpClient;

    public DeviceService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MyDevice>?> GetMyDevicesAsync(CancellationToken ct = default)
    {
        // null=HTTP 失败（GetAsync 返 null），不兜底空 list——否则 VM Devices.Clear() 清空既有列表（网络抖动即丢可见数据）。
        return await _httpClient.GetAsync<List<MyDevice>>("api/app/device/my-devices", ct);
    }

    public async Task<MyDevice> CreateDeviceAsync(string deviceName, string deviceType, CancellationToken ct = default)
    {
        // 写操作：PostAsync 返 null=HTTP 失败 → 抛异常（不兜底空 MyDevice，否则 VM 丢弃返回值后无条件报"创建成功"）。
        // 调用方 AddDeviceAsync 已有 try/catch，抛异常让 catch 生效 → "创建失败"。
        var payload = new { DeviceName = deviceName, DeviceType = deviceType };
        var response = await _httpClient.PostAsync<MyDevice>("api/app/device/device", payload, ct);
        return response ?? throw new HttpRequestException("创建设备失败");
    }
}
