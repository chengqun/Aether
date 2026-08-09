using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.VersionConfig;
using Aether.Server.Application.Contracts.Services.VersionConfig;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Application.Services.VersionConfig;

[Authorize]
public class VersionConfigAppService : CrudAppService<AppVersionConfig, VersionConfigDto, Guid, PagedAndSortedResultRequestDto, VersionConfigCreateDto, VersionConfigUpdateDto>, IVersionConfigAppService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataFilter<IMultiTenant> _multiTenantFilter;

    public VersionConfigAppService(IRepository<AppVersionConfig, Guid> repository, IConfiguration configuration, IHttpContextAccessor httpContextAccessor, IDataFilter<IMultiTenant> multiTenantFilter)
        : base(repository)
    {
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _multiTenantFilter = multiTenantFilter;
        GetPolicyName = AbpPermissions.VersionConfigs.Default;
        GetListPolicyName = AbpPermissions.VersionConfigs.Default;
        CreatePolicyName = AbpPermissions.VersionConfigs.Create;
        UpdatePolicyName = AbpPermissions.VersionConfigs.Update;
        DeletePolicyName = AbpPermissions.VersionConfigs.Delete;
    }

    [Authorize(AbpPermissions.VersionConfigs.Create)]
    public override Task<VersionConfigDto> CreateAsync(VersionConfigCreateDto input)
    {
        return base.CreateAsync(input);
    }

    [Authorize(AbpPermissions.VersionConfigs.Update)]
    public override Task<VersionConfigDto> UpdateAsync(Guid id, VersionConfigUpdateDto input)
    {
        return base.UpdateAsync(id, input);
    }

    [Authorize(AbpPermissions.VersionConfigs.Delete)]
    public override Task DeleteAsync(Guid id)
    {
        return base.DeleteAsync(id);
    }

    // host admin（无租户）跨租户查看所有版本；租户管理员只看本租户版本
    [Authorize(AbpPermissions.VersionConfigs.Default)]
    public override async Task<VersionConfigDto> GetAsync(Guid id)
    {
        if (CurrentUser.TenantId == null)
        {
            using (_multiTenantFilter.Disable())
            {
                return await base.GetAsync(id);
            }
        }
        return await base.GetAsync(id);
    }

    [Authorize(AbpPermissions.VersionConfigs.Default)]
    public override async Task<PagedResultDto<VersionConfigDto>> GetListAsync(PagedAndSortedResultRequestDto input)
    {
        if (CurrentUser.TenantId == null)
        {
            using (_multiTenantFilter.Disable())
            {
                return await base.GetListAsync(input);
            }
        }
        return await base.GetListAsync(input);
    }

    [AllowAnonymous]
    public async Task<CheckUpdateResultDto> CheckUpdateAsync(CheckUpdateInputDto input)
    {
        var queryable = await Repository.GetQueryableAsync();
        var latestVersion = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(v => v.Platform == input.Platform)
                .OrderByDescending(v => v.VersionCode)
        );

        if (latestVersion == null || latestVersion.VersionCode <= input.CurrentVersionCode)
        {
            return new CheckUpdateResultDto { HasUpdate = false };
        }

        return new CheckUpdateResultDto
        {
            HasUpdate = true,
            VersionCode = latestVersion.VersionCode,
            VersionName = latestVersion.VersionName,
            UpdateUrl = latestVersion.UpdateUrl,
            UpdateNote = latestVersion.UpdateNote,
            IsForce = latestVersion.IsForce
        };
    }

    [AllowAnonymous]
    public async Task PublishAsync(VersionConfigPublishDto input)
    {
        var token = _configuration["VersionConfig:PublishToken"];
        if (string.IsNullOrWhiteSpace(token) || !_httpContextAccessor.HttpContext!.Request.Headers.TryGetValue("X-Publish-Token", out var requestToken) || requestToken != token)
        {
            throw new UnauthorizedAccessException("Invalid publish token");
        }

        var entity = new AppVersionConfig(
            (AppPlatform)input.Platform,
            input.VersionCode,
            input.VersionName,
            input.UpdateUrl,
            input.UpdateNote,
            input.IsForce
        );

        await Repository.InsertAsync(entity);
    }
}