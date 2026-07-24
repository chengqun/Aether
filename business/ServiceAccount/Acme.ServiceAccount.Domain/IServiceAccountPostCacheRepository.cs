using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Domain;

public interface IServiceAccountPostCacheRepository
{
    Task<List<ServiceAccountPostCacheItem>> GetAllAsync(CancellationToken ct = default);
    Task<List<ServiceAccountPostCacheItem>> GetByServiceAccountIdAsync(Guid serviceAccountId, CancellationToken ct = default);
    Task<DateTime?> GetLatestPublishedAtAsync(CancellationToken ct = default);
    Task AddOrUpdateAsync(IEnumerable<ServiceAccountPostCacheItem> items, CancellationToken ct = default);
    Task AddAsync(ServiceAccountPostCacheItem item, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}