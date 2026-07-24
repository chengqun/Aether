using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.DeviceManagement.Domain;

public interface IDeviceConfigCacheRepository
{
    Task<DeviceConfigCacheItem?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<DeviceConfigCacheItem?> GetAnyAsync(CancellationToken ct = default);
    Task UpsertAsync(DeviceConfigCacheItem item, CancellationToken ct = default);
}