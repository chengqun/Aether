using Acme.ServiceAccount.Domain;
using Acme.AppAuth.Domain;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Application;

public class ServiceAccountService : IServiceAccountService
{
    private readonly IAppServerHttpClient _httpClient;

    public ServiceAccountService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // 读操作：GetAsync 返 null=HTTP 失败，直接透传（不兜底 ?? new）。空列表/空对象=成功无数据由调用方区分。
    // 写操作 Follow：同样返 nullable（null=失败），让 VM 的 null 检查生效（之前 ?? new 空对象导致 if(result!=null) 永真 → 误报关注成功）。

    public async Task<List<ServiceAccountInfo>?> SearchAsync(string keyword)
        => await _httpClient.GetAsync<List<ServiceAccountInfo>>($"api/app/service-account/search?keyword={Uri.EscapeDataString(keyword)}");

    public async Task<ServiceAccountInfo?> GetAsync(Guid id)
        => await _httpClient.GetAsync<ServiceAccountInfo>($"api/app/service-account/{id}");

    public async Task<List<ServiceAccountInfo>?> GetByCategoryAsync(string category)
        => await _httpClient.GetAsync<List<ServiceAccountInfo>>($"api/app/service-account/by-category?category={Uri.EscapeDataString(category)}");

    public async Task<ServiceAccountInfo?> FollowAsync(Guid serviceAccountId)
        => await _httpClient.PostAsync<ServiceAccountInfo>($"api/app/service-account/follow/{serviceAccountId}", new object());

    public async Task<bool> UnfollowAsync(Guid serviceAccountId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/service-account/unfollow/{serviceAccountId}", new object());
        return response != null && response.IsSuccessStatusCode;
    }

    public async Task<List<ServiceAccountInfo>?> GetRecommendedAsync()
        => await _httpClient.GetAsync<List<ServiceAccountInfo>>("api/app/service-account/all");

    public async Task<List<ServiceAccountPostListItem>?> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null)
    {
        var url = $"api/app/service-account/{serviceAccountId}/posts?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<ServiceAccountPostListItem>>(url);
    }

    public async Task<ServiceAccountPost?> GetPostAsync(Guid postId)
        => await _httpClient.GetAsync<ServiceAccountPost>($"api/app/service-account/post/{postId}");

    public async Task<List<ServiceAccountPostListItem>?> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null)
    {
        var url = $"api/app/service-account/followed-posts?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<ServiceAccountPostListItem>>(url);
    }
}
