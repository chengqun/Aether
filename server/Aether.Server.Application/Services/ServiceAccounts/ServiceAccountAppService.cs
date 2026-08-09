using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.ServiceAccounts;
using Aether.Server.Application.Contracts.Services.ServiceAccounts;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Relationships;
using Aether.Server.Domain.Services.ServiceAccounts;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;

namespace Aether.Server.Application.Services.ServiceAccounts;

/// <summary>
/// 服务号应用服务
/// </summary>
[RequiresFeature("Chat.ServiceAccount")]
[Route("api/app/service-account")]
public class ServiceAccountAppService : ApplicationService, IServiceAccountAppService
{
    private readonly IServiceAccountManager _serviceAccountManager;
    private readonly IServiceAccountPostManager _postManager;
    private readonly IRelationshipManager<AppRelationship, AppServiceAccount, Guid> _relationshipManager;
    private readonly IRepository<AppRelationship, Guid> _relationshipRepository;

    public ServiceAccountAppService(
        IServiceAccountManager serviceAccountManager,
        IServiceAccountPostManager postManager,
        IRelationshipManager<AppRelationship, AppServiceAccount, Guid> relationshipManager,
        IRepository<AppRelationship, Guid> relationshipRepository)
    {
        _serviceAccountManager = serviceAccountManager;
        _postManager = postManager;
        _relationshipManager = relationshipManager;
        _relationshipRepository = relationshipRepository;
    }

    #region 服务号 CRUD（管理员）

    [HttpPost]
    [Authorize(AbpPermissions.ServiceAccounts.Create)]
    public async Task<ServiceAccountDto> CreateAsync(CreateServiceAccountDto input)
    {
        var serviceAccount = await _serviceAccountManager.CreateAsync(
            input.Name,
            CurrentUser.Id.Value,
            input.AvatarUrl,
            input.Description,
            input.Category
        );
        return await MapToDtoAsync(serviceAccount);
    }

    [HttpPut("{id}")]
    [Authorize(AbpPermissions.ServiceAccounts.Update)]
    public async Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountDto input)
    {
        var serviceAccount = await _serviceAccountManager.UpdateAsync(
            id,
            input.Name,
            input.AvatarUrl,
            input.Description,
            input.Category,
            input.IsVerified,
            input.WelcomeMessage
        );
        return await MapToDtoAsync(serviceAccount);
    }

    [HttpDelete("{id}")]
    [Authorize(AbpPermissions.ServiceAccounts.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        await _serviceAccountManager.DeleteAsync(id);
    }

    [HttpGet("{id}")]
    [Authorize]
    public async Task<ServiceAccountDto> GetAsync(Guid id)
    {
        var serviceAccount = await _serviceAccountManager.GetAsync(id);
        return await MapToDtoAsync(serviceAccount);
    }

    [HttpGet("all")]
    [Authorize]
    public async Task<List<ServiceAccountDto>> GetAllAsync()
    {
        var serviceAccounts = await _serviceAccountManager.GetAllAsync();
        var dtos = new List<ServiceAccountDto>();
        foreach (var sa in serviceAccounts)
        {
            dtos.Add(await MapToDtoAsync(sa));
        }
        return dtos;
    }

    [HttpGet("search")]
    [Authorize]
    public async Task<List<ServiceAccountDto>> SearchAsync(string keyword)
    {
        var serviceAccounts = await _serviceAccountManager.SearchAsync(keyword);
        var dtos = new List<ServiceAccountDto>();
        foreach (var sa in serviceAccounts)
        {
            dtos.Add(await MapToDtoAsync(sa));
        }
        return dtos;
    }

    [HttpGet("by-category")]
    [Authorize]
    public async Task<List<ServiceAccountDto>> GetByCategoryAsync(string category)
    {
        var serviceAccounts = await _serviceAccountManager.GetByCategoryAsync(category);
        var dtos = new List<ServiceAccountDto>();
        foreach (var sa in serviceAccounts)
        {
            dtos.Add(await MapToDtoAsync(sa));
        }
        return dtos;
    }

    #endregion

    #region 用户关注/取消关注

    [HttpPost("follow/{serviceAccountId}")]
    [Authorize]
    public async Task<ServiceAccountDto> FollowAsync(Guid serviceAccountId)
    {
        await _relationshipManager.CreateRelationshipAsync(CurrentUser.Id.Value, serviceAccountId.ToString());
        var serviceAccount = await _serviceAccountManager.GetAsync(serviceAccountId);
        return await MapToDtoAsync(serviceAccount);
    }

    [HttpPost("unfollow/{serviceAccountId}")]
    [Authorize]
    public async Task UnfollowAsync(Guid serviceAccountId)
    {
        var relationship = await _relationshipRepository.FirstOrDefaultAsync(
            r => r.UserId == CurrentUser.Id.Value
            && r.TargetType == Domain.Shared.Enums.RelationshipType.ServiceAccount
            && r.TargetId == serviceAccountId.ToString()
        );
        if (relationship != null)
        {
            await _relationshipManager.RemoveRelationshipAsync(relationship.Id);
        }
    }

    #endregion

    #region 服务号内容管理

    [HttpPost("post")]
    [Authorize(AbpPermissions.ServiceAccounts.Publish)]
    public async Task<ServiceAccountPostDto> PublishPostAsync(CreateServiceAccountPostDto input)
    {
        var post = await _postManager.CreatePostAsync(
            input.ServiceAccountId,
            input.Title,
            input.Content,
            input.Summary,
            input.CoverImageUrl,
            input.ContentType
        );
        return await MapPostToDtoAsync(post);
    }

    [HttpDelete("post/{postId}")]
    [Authorize(AbpPermissions.ServiceAccounts.Publish)]
    public async Task DeletePostAsync(Guid postId)
    {
        await _postManager.DeletePostAsync(postId);
    }

    [HttpGet("post/{postId}")]
    [Authorize]
    public async Task<ServiceAccountPostDto> GetPostAsync(Guid postId)
    {
        var post = await _postManager.GetPostAsync(postId);
        return await MapPostToDtoAsync(post);
    }

    [HttpGet("{serviceAccountId}/posts")]
    [Authorize]
    public async Task<List<ServiceAccountPostListItemDto>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null)
    {
        var posts = await _postManager.GetPostsAsync(serviceAccountId, limit, offset, since);
        return await MapPostListItemsAsync(posts);
    }

    [HttpGet("followed-posts")]
    [Authorize]
    public async Task<List<ServiceAccountPostListItemDto>> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null)
    {
        // 获取当前用户关注的服务号
        var relationships = await _relationshipRepository.GetListAsync(
            r => r.UserId == CurrentUser.Id.Value
            && r.TargetType == Domain.Shared.Enums.RelationshipType.ServiceAccount
        );

        var serviceAccountIds = relationships
            .Select(r => Guid.Parse(r.TargetId))
            .ToList();

        var posts = await _postManager.GetFollowedPostsAsync(serviceAccountIds, limit, offset, since);
        return await MapPostListItemsAsync(posts);
    }

    [HttpPost("post/{postId}/pin")]
    [Authorize(AbpPermissions.ServiceAccounts.Publish)]
    public async Task PinPostAsync(Guid postId)
    {
        await _postManager.PinPostAsync(postId);
    }

    [HttpPost("post/{postId}/unpin")]
    [Authorize(AbpPermissions.ServiceAccounts.Publish)]
    public async Task UnpinPostAsync(Guid postId)
    {
        await _postManager.UnpinPostAsync(postId);
    }

    #endregion

    #region 映射方法

    private async Task<ServiceAccountDto> MapToDtoAsync(AppServiceAccount serviceAccount)
    {
        // 计算关注者数量
        var followers = await _relationshipRepository.GetListAsync(
            r => r.TargetType == Domain.Shared.Enums.RelationshipType.ServiceAccount
            && r.TargetId == serviceAccount.Id.ToString()
        );
        var followerCount = followers.Count;

        // 检查当前用户是否已关注
        var isFollowed = await _relationshipManager.RelationshipExistsAsync(
            CurrentUser.Id.Value,
            serviceAccount.Id.ToString()
        );

        return new ServiceAccountDto
        {
            Id = serviceAccount.Id,
            Name = serviceAccount.Name,
            AvatarUrl = serviceAccount.AvatarUrl,
            Description = serviceAccount.Description,
            Category = serviceAccount.Category,
            IsVerified = serviceAccount.IsVerified,
            WelcomeMessage = serviceAccount.WelcomeMessage,
            CreationTime = serviceAccount.CreationTime,
            FollowerCount = (int)followerCount,
            IsFollowedByCurrentUser = isFollowed
        };
    }

    private async Task<ServiceAccountPostDto> MapPostToDtoAsync(AppServiceAccountPost post)
    {
        var serviceAccount = await _serviceAccountManager.GetAsync(post.ServiceAccountId);
        return new ServiceAccountPostDto
        {
            Id = post.Id,
            ServiceAccountId = post.ServiceAccountId,
            Title = post.Title,
            Summary = post.Summary,
            Content = post.Content,
            CoverImageUrl = post.CoverImageUrl,
            ContentType = post.ContentType,
            IsPinned = post.IsPinned,
            PublishedAt = post.PublishedAt,
            ServiceAccountName = serviceAccount.Name,
            ServiceAccountAvatarUrl = serviceAccount.AvatarUrl
        };
    }

    private async Task<List<ServiceAccountPostListItemDto>> MapPostListItemsAsync(List<AppServiceAccountPost> posts)
    {
        // 批量获取服务号信息，避免 N+1 查询
        var serviceAccountIds = posts.Select(p => p.ServiceAccountId).Distinct().ToList();
        var allServiceAccounts = await _serviceAccountManager.GetAllAsync();
        var saDict = allServiceAccounts.Where(sa => serviceAccountIds.Contains(sa.Id))
            .ToDictionary(sa => sa.Id, sa => sa);

        return posts.Select(p =>
        {
            saDict.TryGetValue(p.ServiceAccountId, out var sa);
            return new ServiceAccountPostListItemDto
            {
                Id = p.Id,
                ServiceAccountId = p.ServiceAccountId,
                Title = p.Title,
                Summary = p.Summary,
                CoverImageUrl = p.CoverImageUrl,
                ContentType = p.ContentType,
                IsPinned = p.IsPinned,
                PublishedAt = p.PublishedAt,
                ServiceAccountName = sa?.Name ?? ""
            };
        }).ToList();
    }

    #endregion
}
