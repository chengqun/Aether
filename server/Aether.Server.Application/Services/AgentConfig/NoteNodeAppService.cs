using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Aether.Server.Application.Contracts.Services.AgentConfig;
using Aether.Server.Domain.Entities;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Application.Services.AgentConfig;

/// <summary>
/// NoteNode 管理 CRUD（Blazor admin）。替代旧 AgentConfigAppService。
/// host admin（TenantId=null）跨租户看全部；租户 admin 仅本租户（ABP 自动过滤）。
/// </summary>
[Authorize]
public class NoteNodeAppService : CrudAppService<
    AppNoteNode,
    NoteNodeDto,
    Guid,
    PagedAndSortedResultRequestDto,
    NoteNodeCreateDto,
    NoteNodeUpdateDto>,
    INoteNodeAppService
{
    private readonly IdentityUserManager _userManager;
    private readonly IDataFilter<IMultiTenant> _multiTenantFilter;

    public NoteNodeAppService(IRepository<AppNoteNode, Guid> repository, IdentityUserManager userManager,
        IDataFilter<IMultiTenant> multiTenantFilter)
        : base(repository)
    {
        _userManager = userManager;
        _multiTenantFilter = multiTenantFilter;
        GetPolicyName = AbpPermissions.AgentConfigs.Default;
        GetListPolicyName = AbpPermissions.AgentConfigs.Default;
        CreatePolicyName = AbpPermissions.AgentConfigs.Create;
        UpdatePolicyName = AbpPermissions.AgentConfigs.Update;
        DeletePolicyName = AbpPermissions.AgentConfigs.Delete;
    }

    public override async Task<PagedResultDto<NoteNodeDto>> GetListAsync(PagedAndSortedResultRequestDto input)
    {
        if (CurrentUser.TenantId == null)
        {
            using (_multiTenantFilter.Disable())
                return await base.GetListAsync(input);
        }
        return await base.GetListAsync(input);
    }

    public override async Task<NoteNodeDto> GetAsync(Guid id)
    {
        if (CurrentUser.TenantId == null)
        {
            using (_multiTenantFilter.Disable())
                return await base.GetAsync(id);
        }
        return await base.GetAsync(id);
    }

    protected override async Task<NoteNodeDto> MapToGetOutputDtoAsync(AppNoteNode entity)
    {
        var dto = await base.MapToGetOutputDtoAsync(entity);
        if (entity.CreatorId.HasValue)
        {
            var user = await _userManager.FindByIdAsync(entity.CreatorId.Value.ToString());
            dto.CreatorUserName = user?.UserName;
        }
        dto.LastModifiedAt = entity.LastModificationTime ?? entity.CreationTime;
        return dto;
    }

    protected override IQueryable<AppNoteNode> ApplyDefaultSorting(IQueryable<AppNoteNode> query)
        => query.OrderBy(n => n.Title);
}
