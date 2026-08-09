using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.ServiceAccounts;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.ServiceAccounts;

/// <summary>
/// 服务号应用服务接口
/// </summary>
public interface IServiceAccountAppService : IApplicationService
{
    // 服务号 CRUD（管理员）
    Task<ServiceAccountDto> CreateAsync(CreateServiceAccountDto input);
    Task<ServiceAccountDto> UpdateAsync(Guid id, UpdateServiceAccountDto input);
    Task DeleteAsync(Guid id);
    Task<ServiceAccountDto> GetAsync(Guid id);
    Task<List<ServiceAccountDto>> GetAllAsync();
    Task<List<ServiceAccountDto>> SearchAsync(string keyword);
    Task<List<ServiceAccountDto>> GetByCategoryAsync(string category);

    // 用户关注/取消关注
    Task<ServiceAccountDto> FollowAsync(Guid serviceAccountId);
    Task UnfollowAsync(Guid serviceAccountId);

    // 服务号内容管理
    Task<ServiceAccountPostDto> PublishPostAsync(CreateServiceAccountPostDto input);
    Task DeletePostAsync(Guid postId);
    Task<ServiceAccountPostDto> GetPostAsync(Guid postId);
    Task<List<ServiceAccountPostListItemDto>> GetPostsAsync(Guid serviceAccountId, int limit = 50, int offset = 0, DateTime? since = null);
    Task<List<ServiceAccountPostListItemDto>> GetFollowedPostsAsync(int limit = 50, int offset = 0, DateTime? since = null);
    Task PinPostAsync(Guid postId);
    Task UnpinPostAsync(Guid postId);
}
