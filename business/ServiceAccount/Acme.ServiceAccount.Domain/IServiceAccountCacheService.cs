using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Domain;

public interface IServiceAccountCacheService
{
    Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(CancellationToken ct = default);
    Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(Guid serviceAccountId, CancellationToken ct = default);
    Task<DateTime?> GetLatestCachedPublishedAtAsync(CancellationToken ct = default);
    /// <summary>指定服务号下最新文章发布时间（按账号隔离 since 游标）。</summary>
    Task<DateTime?> GetLatestCachedPublishedAtAsync(Guid serviceAccountId, CancellationToken ct = default);
    Task UpdateCacheAsync(List<ServiceAccountPostListItem> posts, CancellationToken ct = default);
    Task AddPostAsync(ServiceAccountPostListItem post, CancellationToken ct = default);
    Task ClearCacheAsync(CancellationToken ct = default);
}
