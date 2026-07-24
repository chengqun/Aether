using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Domain;

public interface IServiceAccountCacheService
{
    Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync();
    Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(Guid serviceAccountId);
    Task<DateTime?> GetLatestCachedPublishedAtAsync();
    Task UpdateCacheAsync(List<ServiceAccountPostListItem> posts);
    Task AddPostAsync(ServiceAccountPostListItem post);
    Task ClearCacheAsync();
}