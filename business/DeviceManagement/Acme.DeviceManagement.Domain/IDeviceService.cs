using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.DeviceManagement.Domain;

public interface IDeviceService
{
    /// <summary>已绑定设备列表。null=HTTP 失败（不兜底空 list，否则 VM 清空既有列表=数据丢失），空列表=成功无设备。</summary>
    Task<List<MyDevice>?> GetMyDevicesAsync(CancellationToken ct = default);
    /// <summary>创建/绑定设备。失败抛 HttpRequestException——调用方 try/catch 处理（不兜底空对象，否则 VM 误报创建成功）。</summary>
    Task<MyDevice> CreateDeviceAsync(string deviceName, string deviceType, CancellationToken ct = default);
}
