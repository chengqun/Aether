using Acme.ServiceAccount.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Acme.ServiceAccount.Application;

public class ServiceAccountCacheService : IServiceAccountCacheService
{
    private readonly IServiceAccountPostCacheRepository _repository;

    public ServiceAccountCacheService(IServiceAccountPostCacheRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync()
    {
        var items = await _repository.GetAllAsync();
        return items.Select(ToDto).ToList();
    }

    public async Task<List<ServiceAccountPostListItem>> GetCachedPostsAsync(Guid serviceAccountId)
    {
        var items = await _repository.GetByServiceAccountIdAsync(serviceAccountId);
        return items.Select(ToDto).ToList();
    }

    public async Task<DateTime?> GetLatestCachedPublishedAtAsync()
    {
        return await _repository.GetLatestPublishedAtAsync();
    }

    public async Task UpdateCacheAsync(List<ServiceAccountPostListItem> posts)
    {
        var items = posts.Select(ToItem);
        await _repository.AddOrUpdateAsync(items);
    }

    public async Task AddPostAsync(ServiceAccountPostListItem post)
    {
        await _repository.AddAsync(ToItem(post));
    }

    public async Task ClearCacheAsync()
    {
        await _repository.ClearAllAsync();
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