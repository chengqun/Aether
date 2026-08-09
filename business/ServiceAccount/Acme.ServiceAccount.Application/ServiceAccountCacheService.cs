using Acme.ServiceAccount.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Application;

public class ServiceAccountCacheService : IServiceAccountCacheService
{
    private readonly IServiceAccountPostCacheRepository _repository;

    public ServiceAccountCacheService(IServiceAccountPostCacheRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(CancellationToken ct = default)
    {
        var items = await _repository.GetAllAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(Guid serviceAccountId, CancellationToken ct = default)
    {
        var items = await _repository.GetByServiceAccountIdAsync(serviceAccountId, ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<DateTime?> GetLatestCachedPublishedAtAsync(CancellationToken ct = default)
    {
        return await _repository.GetLatestPublishedAtAsync(ct);
    }

    public async Task<DateTime?> GetLatestCachedPublishedAtAsync(Guid serviceAccountId, CancellationToken ct = default)
    {
        return await _repository.GetLatestPublishedAtAsync(serviceAccountId, ct);
    }

    public async Task UpdateCacheAsync(List<ServiceAccountPostListItem> posts, CancellationToken ct = default)
    {
        var items = posts.Select(ToItem);
        await _repository.AddOrUpdateAsync(items, ct);
    }

    public async Task AddPostAsync(ServiceAccountPostListItem post, CancellationToken ct = default)
    {
        await _repository.AddAsync(ToItem(post), ct);
    }

    public async Task ClearCacheAsync(CancellationToken ct = default)
    {
        await _repository.ClearAllAsync(ct);
    }

    private static ServiceAccountPostListItem ToDto(ServiceAccountPostCacheItem item) => new()
    {
        Id = item.Id,
        ServiceAccountId = item.ServiceAccountId,
        Title = item.Title,
        Summary = item.Summary,
        CoverImageUrl = item.CoverImageUrl,
        ContentType = item.ContentType,
        IsPinned = item.IsPinned,
        PublishedAt = item.PublishedAt,
        ServiceAccountName = item.ServiceAccountName
    };

    private static ServiceAccountPostCacheItem ToItem(ServiceAccountPostListItem dto) => new()
    {
        Id = dto.Id,
        ServiceAccountId = dto.ServiceAccountId,
        Title = dto.Title,
        Summary = dto.Summary,
        CoverImageUrl = dto.CoverImageUrl,
        ContentType = dto.ContentType,
        IsPinned = dto.IsPinned,
        PublishedAt = dto.PublishedAt,
        ServiceAccountName = dto.ServiceAccountName
    };
}
