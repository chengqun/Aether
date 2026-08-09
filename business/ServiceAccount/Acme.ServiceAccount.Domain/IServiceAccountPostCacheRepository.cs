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
    /// <summary>指定服务号下最新文章发布时间（按账号隔离 since 游标，避免多服务号切换时全局 MAX 过滤掉较旧账号的新文章）。</summary>
    Task<DateTime?> GetLatestPublishedAtAsync(Guid serviceAccountId, CancellationToken ct = default);
    Task AddOrUpdateAsync(IEnumerable<ServiceAccountPostCacheItem> items, CancellationToken ct = default);
    Task AddAsync(ServiceAccountPostCacheItem item, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}