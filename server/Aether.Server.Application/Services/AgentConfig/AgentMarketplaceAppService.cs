using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Aether.Server.Application.Contracts.Services.AgentConfig;
using Aether.Server.Domain.Entities;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp.Identity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.AgentConfig;

/// <summary>
/// Agent 市场服务：发布 NoteNode（Agent/Workflow 源文本）到公共市场，登录用户可召唤，admin 可发布/下架。
/// 数据存 AppNoteNode(IsPublished=true, NodeType=Agent/Workflow)。
/// 纯 ABP convention routing。路由：GET marketplace / GET {id} / POST publish-local /
/// POST {id}/sync-local / POST {id}/unpublish-local / POST {id}/publish / POST {id}/unpublish
/// </summary>
[Authorize]
public class AgentMarketplaceAppService : ApplicationService, IAgentMarketplaceAppService
{
    private readonly IRepository<AppNoteNode, Guid> _noteNodeRepository;
    private readonly IdentityUserManager _userManager;
    private readonly IDataFilter<IMultiTenant> _multiTenantFilter;

    public AgentMarketplaceAppService(
        IRepository<AppNoteNode, Guid> noteNodeRepository,
        IdentityUserManager userManager,
        IDataFilter<IMultiTenant> multiTenantFilter)
    {
        _noteNodeRepository = noteNodeRepository;
        _userManager = userManager;
        _multiTenantFilter = multiTenantFilter;
    }

    /// <inheritdoc />
    public async Task<ListResultDto<MarketplaceAgentDto>> GetMarketplaceAsync()
    {
        var queryable = await _noteNodeRepository.GetQueryableAsync();
        var tenantId = CurrentUser.TenantId;

        IQueryable<AppNoteNode> filteredQuery;
        if (tenantId.HasValue)
        {
            filteredQuery = queryable.Where(n => n.IsPublished
                && (n.NodeType == "Agent" || n.NodeType == "Workflow")
                && (n.TenantId == tenantId.Value || n.TenantId == null));
        }
        else
        {
            using (_multiTenantFilter.Disable())
            {
                filteredQuery = queryable.Where(n => n.IsPublished
                    && (n.NodeType == "Agent" || n.NodeType == "Workflow"));
            }
        }

        var nodes = await AsyncExecuter.ToListAsync(
            filteredQuery.OrderByDescending(n => n.CreationTime)
        );

        var dtos = new List<MarketplaceAgentDto>();
        foreach (var n in nodes)
        {
            dtos.Add(new MarketplaceAgentDto
            {
                Id = n.Id,
                AgentName = n.Title,
                Description = n.Description,
                Category = string.IsNullOrEmpty(n.Category) ? "普通对话" : n.Category,
                MinAppVersion = n.MinAppVersion,
                CreatorUserName = await GetUserNameAsync(n.CreatorId),
                LastModifiedAt = n.LastModificationTime ?? n.CreationTime,
                Kind = string.IsNullOrEmpty(n.NodeType) ? "Agent" : n.NodeType,
            });
        }

        return new ListResultDto<MarketplaceAgentDto>(dtos);
    }

    /// <inheritdoc />
    public async Task<MarketplaceAgentDetailDto> GetAsync(Guid id)
    {
        var tenantId = CurrentUser.TenantId;
        AppNoteNode? entity;

        if (tenantId.HasValue)
        {
            using (_multiTenantFilter.Disable())
            {
                entity = await _noteNodeRepository.FirstOrDefaultAsync(n =>
                    n.Id == id && n.IsPublished
                    && (n.TenantId == tenantId.Value || n.TenantId == null));
            }
        }
        else
        {
            using (_multiTenantFilter.Disable())
            {
                entity = await _noteNodeRepository.FirstOrDefaultAsync(n => n.Id == id && n.IsPublished);
            }
        }

        if (entity == null)
        {
            throw new BusinessException("Aether:MarketplaceAgentNotFound");
        }

        return new MarketplaceAgentDetailDto
        {
            Id = entity.Id,
            AgentName = entity.Title,
            Description = entity.Description,
            Category = string.IsNullOrEmpty(entity.Category) ? "普通对话" : entity.Category,
            Content = entity.Content ?? string.Empty,
            MinAppVersion = entity.MinAppVersion,
            LastModifiedAt = entity.LastModificationTime ?? entity.CreationTime,
            Kind = string.IsNullOrEmpty(entity.NodeType) ? "Agent" : entity.NodeType,
        };
    }

    /// <inheritdoc />
    [Authorize(AbpPermissions.AgentConfigs.Manage)]
    [RequiresFeature("Marketplace.Publish")]
    public async Task<Guid> PublishLocalAsync(PublishLocalAgentInputDto input)
    {
        var userId = CurrentUser.GetId();
        var tenantId = CurrentUser.TenantId;
        var nodeType = string.IsNullOrEmpty(input.Kind) ? "Agent" : input.Kind;

        // 按 (CreatorId, Title, NodeType, TenantId) 幂等：同名同类型更新并上架
        var queryable = await _noteNodeRepository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(n => n.CreatorId == userId && n.Title == input.AgentName
                && n.NodeType == nodeType && n.TenantId == tenantId)
        );

        if (existing != null)
        {
            ApplyLocalInput(existing, input, nodeType);
            existing.IsPublished = true;
            await _noteNodeRepository.UpdateAsync(existing);
            return existing.Id;
        }

        var entity = new AppNoteNode(input.AgentName ?? string.Empty, input.Content ?? string.Empty, nodeType);
        ApplyLocalInput(entity, input, nodeType);
        entity.IsPublished = true;
        await _noteNodeRepository.InsertAsync(entity);
        return entity.Id;
    }

    /// <inheritdoc />
    [Authorize(AbpPermissions.AgentConfigs.Manage)]
    [RequiresFeature("Marketplace.Publish")]
    public async Task SyncLocalAsync(Guid id, PublishLocalAgentInputDto input)
    {
        var userId = CurrentUser.GetId();
        var entity = await _noteNodeRepository.GetAsync(id);
        if (entity == null || entity.CreatorId != userId)
        {
            throw new BusinessException("Aether:MarketplaceAgentNotFound");
        }
        var nodeType = string.IsNullOrEmpty(input.Kind) ? entity.NodeType : input.Kind;
        ApplyLocalInput(entity, input, nodeType);
        entity.IsPublished = true;
        await _noteNodeRepository.UpdateAsync(entity);
    }

    /// <inheritdoc />
    [Authorize(AbpPermissions.AgentConfigs.Manage)]
    public async Task UnpublishLocalAsync(Guid id)
    {
        var userId = CurrentUser.GetId();
        var entity = await _noteNodeRepository.GetAsync(id);
        if (entity == null || entity.CreatorId != userId)
        {
            throw new BusinessException("Aether:MarketplaceAgentNotFound");
        }
        entity.IsPublished = false;
        await _noteNodeRepository.UpdateAsync(entity);
    }

    /// <inheritdoc />
    [Authorize(AbpPermissions.AgentConfigs.Manage)]
    [RequiresFeature("Marketplace.Publish")]
    public async Task PublishAsync(Guid id)
    {
        var entity = await FindCrossTenantAsync(id);
        if (entity == null)
        {
            throw new BusinessException("Aether:AgentConfigNotFound");
        }
        entity.IsPublished = true;
        await _noteNodeRepository.UpdateAsync(entity);
    }

    /// <inheritdoc />
    [Authorize(AbpPermissions.AgentConfigs.Manage)]
    public async Task UnpublishAsync(Guid id)
    {
        var entity = await FindCrossTenantAsync(id);
        if (entity == null)
        {
            throw new BusinessException("Aether:AgentConfigNotFound");
        }
        entity.IsPublished = false;
        await _noteNodeRepository.UpdateAsync(entity);
    }

    private async Task<AppNoteNode?> FindCrossTenantAsync(Guid id)
    {
        if (CurrentUser.TenantId == null)
        {
            using (_multiTenantFilter.Disable())
            {
                return await _noteNodeRepository.FindAsync(id);
            }
        }
        return await _noteNodeRepository.FindAsync(id);
    }

    private static void ApplyLocalInput(AppNoteNode entity, PublishLocalAgentInputDto input, string nodeType)
    {
        entity.Title = input.AgentName ?? entity.Title;
        entity.NodeType = nodeType;
        entity.Content = input.Content ?? string.Empty;
        entity.Description = input.Description ?? string.Empty;
        entity.Category = string.IsNullOrEmpty(input.Category) ? "普通对话" : input.Category;
        entity.MinAppVersion = input.MinAppVersion;
    }

    private async Task<string?> GetUserNameAsync(Guid? creatorId)
    {
        if (!creatorId.HasValue) return null;
        var user = await _userManager.FindByIdAsync(creatorId.Value.ToString());
        return user?.UserName;
    }
}
