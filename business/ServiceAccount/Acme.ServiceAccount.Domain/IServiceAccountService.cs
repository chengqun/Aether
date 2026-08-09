using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Domain;

public interface IServiceAccountService
{
    /// <summary>搜索服务号。null=HTTP 失败，空列表=成功无结果。</summary>
    Task<List<ServiceAccountInfo>?> SearchAsync(string keyword);
    /// <summary>服务号详情。null=HTTP 失败——不兜底空对象（空对象有合法形状 Guid.Empty，调用方 != null 检查会永真）。</summary>
    Task<ServiceAccountInfo?> GetAsync(Guid id);
    Task<List<ServiceAccountInfo>?> GetByCategoryAsync(string category);
    /// <summary>关注。null=HTTP 失败——不兜底空对象（否则 VM 误报关注成功+粉丝数+1）。</summary>
    Task<ServiceAccountInfo?> FollowAsync(Guid serviceAccountId);
    Task<bool> UnfollowAsync(Guid serviceAccountId);
    Task<List<ServiceAccountInfo>?> GetRecommendedAsync();
    Task<List<ServiceAccountPostListItem>?> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null);
    /// <summary>文章详情。null=HTTP 失败——不兜底空文章（否则详情页显示空白无错误提示）。</summary>
    Task<ServiceAccountPost?> GetPostAsync(Guid postId);
    Task<List<ServiceAccountPostListItem>?> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null);
}
