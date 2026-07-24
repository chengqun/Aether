using Acme.ServiceAccount.Domain;
using Acme.Platform.Domain;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Application;

public class ServiceAccountService : IServiceAccountService
{
    private readonly IAppServerHttpClient _httpClient;

    public ServiceAccountService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<ServiceAccountInfo>> SearchAsync(string keyword)
    {
        return await _httpClient.GetAsync<List<ServiceAccountInfo>>($"api/app/service-account/search?keyword={Uri.EscapeDataString(keyword)}") ?? new List<ServiceAccountInfo>();
    }

    public async Task<ServiceAccountInfo> GetAsync(Guid id)
    {
        return await _httpClient.GetAsync<ServiceAccountInfo>($"api/app/service-account/{id}") ?? new ServiceAccountInfo();
    }

    public async Task<List<ServiceAccountInfo>> GetByCategoryAsync(string category)
    {
        return await _httpClient.GetAsync<List<ServiceAccountInfo>>($"api/app/service-account/by-category?category={Uri.EscapeDataString(category)}") ?? new List<ServiceAccountInfo>();
    }

    public async Task<ServiceAccountInfo> FollowAsync(Guid serviceAccountId)
    {
        return await _httpClient.PostAsync<ServiceAccountInfo>($"api/app/service-account/follow/{serviceAccountId}", new object()) ?? new ServiceAccountInfo();
    }

    public async Task<bool> UnfollowAsync(Guid serviceAccountId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/service-account/unfollow/{serviceAccountId}", new object());
        return response != null && response.IsSuccessStatusCode;
    }

    public async Task<List<ServiceAccountInfo>> GetRecommendedAsync()
    {
        return await _httpClient.GetAsync<List<ServiceAccountInfo>>("api/app/service-account/all") ?? new List<ServiceAccountInfo>();
    }

    public async Task<List<ServiceAccountPostListItem>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null)
    {
        var url = $"api/app/service-account/{serviceAccountId}/posts?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<ServiceAccountPostListItem>>(url) ?? new List<ServiceAccountPostListItem>();
    }

    public async Task<ServiceAccountPost> GetPostAsync(Guid postId)
    {
        return await _httpClient.GetAsync<ServiceAccountPost>($"api/app/service-account/post/{postId}") ?? new ServiceAccountPost();
    }

    public async Task<List<ServiceAccountPostListItem>> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null)
    {
        var url = $"api/app/service-account/followed-posts?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<ServiceAccountPostListItem>>(url) ?? new List<ServiceAccountPostListItem>();
    }
}