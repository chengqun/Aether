using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Domain;

public interface IServiceAccountService
{
    Task<List<ServiceAccountInfo>> SearchAsync(string keyword);
    Task<ServiceAccountInfo> GetAsync(Guid id);
    Task<List<ServiceAccountInfo>> GetByCategoryAsync(string category);
    Task<ServiceAccountInfo> FollowAsync(Guid serviceAccountId);
    Task<bool> UnfollowAsync(Guid serviceAccountId);
    Task<List<ServiceAccountInfo>> GetRecommendedAsync();
    Task<List<ServiceAccountPostListItem>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null);
    Task<ServiceAccountPost> GetPostAsync(Guid postId);
    Task<List<ServiceAccountPostListItem>> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null);
}