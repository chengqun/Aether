using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Aether.Server.Domain.Services.ServiceAccounts;

/// <summary>
/// 服务号内容管理器接口
/// </summary>
public interface IServiceAccountPostManager
{
    Task<AppServiceAccountPost> CreatePostAsync(Guid serviceAccountId, string title, string content, string? summary = null, string? coverImageUrl = null, string contentType = "Article");
        Task<AppServiceAccountPost> CreatePostAsync(Guid serviceAccountId, string title, string content, DateTime publishedAt, string? summary = null, string? coverImageUrl = null, string contentType = "Article");
    Task DeletePostAsync(Guid postId);
    Task<AppServiceAccountPost> GetPostAsync(Guid postId);
    Task<List<AppServiceAccountPost>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null);
    Task<List<AppServiceAccountPost>> GetPinnedPostsAsync(Guid serviceAccountId);
    Task PinPostAsync(Guid postId);
    Task UnpinPostAsync(Guid postId);
    Task<List<AppServiceAccountPost>> GetFollowedPostsAsync(List<Guid> serviceAccountIds, int limit = 50, int offset = 0, DateTime? since = null);
}

/// <summary>
/// 服务号内容管理器
/// </summary>
public class ServiceAccountPostManager : DomainService, IServiceAccountPostManager
{
    private readonly IRepository<AppServiceAccountPost, Guid> _postRepository;

    public ServiceAccountPostManager(IRepository<AppServiceAccountPost, Guid> postRepository)
    {
        _postRepository = postRepository;
    }

    public async Task<AppServiceAccountPost> CreatePostAsync(Guid serviceAccountId, string title, string content, string? summary = null, string? coverImageUrl = null, string contentType = "Article")
    {
        return await CreatePostAsync(serviceAccountId, title, content, DateTime.UtcNow, summary, coverImageUrl, contentType);
    }

    public async Task<AppServiceAccountPost> CreatePostAsync(Guid serviceAccountId, string title, string content, DateTime publishedAt, string? summary = null, string? coverImageUrl = null, string contentType = "Article")
    {
        var id = GuidGenerator.Create();
        var post = new AppServiceAccountPost(id, serviceAccountId, title, content, contentType)
        {
            Summary = summary,
            CoverImageUrl = coverImageUrl,
            PublishedAt = publishedAt
        };
        return await _postRepository.InsertAsync(post);
    }

    public async Task DeletePostAsync(Guid postId)
    {
        await _postRepository.DeleteAsync(postId);
    }

    public async Task<AppServiceAccountPost> GetPostAsync(Guid postId)
    {
        return await _postRepository.GetAsync(postId);
    }

    public async Task<List<AppServiceAccountPost>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null)
    {
        var posts = await _postRepository.GetListAsync(p =>
            p.ServiceAccountId == serviceAccountId
            && (!since.HasValue || p.PublishedAt > since.Value));
        // 置顶优先，然后按发布时间倒序
        return posts
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.PublishedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    public async Task<List<AppServiceAccountPost>> GetPinnedPostsAsync(Guid serviceAccountId)
    {
        return await _postRepository.GetListAsync(p => p.ServiceAccountId == serviceAccountId && p.IsPinned);
    }

    public async Task PinPostAsync(Guid postId)
    {
        var post = await _postRepository.GetAsync(postId);
        post.Pin();
        await _postRepository.UpdateAsync(post);
    }

    public async Task UnpinPostAsync(Guid postId)
    {
        var post = await _postRepository.GetAsync(postId);
        post.Unpin();
        await _postRepository.UpdateAsync(post);
    }

    public async Task<List<AppServiceAccountPost>> GetFollowedPostsAsync(List<Guid> serviceAccountIds, int limit = 50, int offset = 0, DateTime? since = null)
    {
        if (serviceAccountIds.Count == 0)
            return new List<AppServiceAccountPost>();

        var posts = await _postRepository.GetListAsync(p =>
            serviceAccountIds.Contains(p.ServiceAccountId)
            && (!since.HasValue || p.PublishedAt > since.Value));
        return posts
            .OrderByDescending(p => p.PublishedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }
}