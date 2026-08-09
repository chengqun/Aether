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
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.AgentConfig;

[Authorize]
public class LlmConfigAppService : ApplicationService, ILlmConfigAppService
{
    private readonly IRepository<AppLlmConfig, Guid> _repository;
    private readonly IdentityUserManager _userManager;

    public LlmConfigAppService(IRepository<AppLlmConfig, Guid> repository, IdentityUserManager userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    /// <summary>
    /// 获取当前用户可见的所有 LLM 配置：系统级配置（host 共享，只读）+ 用户私有配置（本租户）
    /// </summary>
    public async Task<ListResultDto<LlmConfigDto>> GetMyLlmConfigsAsync()
    {
        var userId = CurrentUser.GetId();

        // 用户私有配置（当前租户上下文，ABP 自动按租户过滤）
        var userConfigs = await AsyncExecuter.ToListAsync(
            (await _repository.GetQueryableAsync())
                .Where(c => !c.IsSystem && c.CreatorId == userId)
                .OrderByDescending(c => c.IsDefault)
                .ThenBy(c => c.Name)
        );

        // 系统级配置（host 共享，TenantId=null）——切到 host 上下文取，并在 host 上下文里映射（_userManager 才能找到 host admin）
        List<LlmConfigDto> systemDtos;
        using (CurrentTenant.Change(null))
        {
            var systemConfigs = await AsyncExecuter.ToListAsync(
                (await _repository.GetQueryableAsync())
                    .Where(c => c.IsSystem)
                    .OrderByDescending(c => c.IsDefault)
                    .ThenBy(c => c.Name)
            );
            systemDtos = systemConfigs.Select(MapToDto).ToList();
        }

        // 系统配置在前
        return new ListResultDto<LlmConfigDto>(
            systemDtos.Concat(userConfigs.Select(MapToDto)).ToList()
        );
    }

    /// <summary>
    /// 新增或更新用户级 LLM 配置（强制 IsSystem=false，IsDefault=false）
    /// 用户级没有"默认"概念，用户添加的 LLM 需要在 Agent 配置中显式指定。
    /// </summary>
    public async Task<LlmConfigDto> SetMyLlmConfigAsync(LlmConfigCreateDto input)
    {
        var userId = CurrentUser.GetId();

        if (input.Id.HasValue && input.Id.Value != Guid.Empty)
        {
            // 更新
            var config = await _repository.GetAsync(input.Id.Value);
            if (config.CreatorId != userId || config.IsSystem)
                throw new BusinessException("Aether:LlmConfigNotFound");

            config.Name = input.Name;
            config.ModelName = input.ModelName;
            config.ApiKey = input.ApiKey;
            config.Endpoint = input.Endpoint;
            config.Temperature = input.Temperature;
            // 用户级强制 IsDefault=false（忽略客户端传值）

            await _repository.UpdateAsync(config);
            return MapToDto(config);
        }
        else
        {
            // 新增 — 强制 IsSystem=false, IsDefault=false
            var config = new AppLlmConfig(
                input.Name,
                input.ModelName,
                input.ApiKey,
                input.Endpoint,
                input.Temperature,
                isDefault: false,
                isSystem: false
            );

            await _repository.InsertAsync(config);
            return MapToDto(config);
        }
    }

    /// <summary>
    /// 设置用户级默认 LLM 配置 — 用户级不需要默认，此方法已废弃。
    /// 保留接口兼容性，但不再执行实际操作。
    /// </summary>
    public async Task SetDefaultLlmConfigAsync(Guid id)
    {
        // 用户级 LLM 配置没有"默认"概念
        // 保留此方法以兼容接口，但不执行任何操作
        await Task.CompletedTask;
    }

    /// <summary>
    /// 删除用户级 LLM 配置（检查是否有 Agent 在用）
    /// </summary>
    public async Task DeleteLlmConfigAsync(Guid id)
    {
        var userId = CurrentUser.GetId();
        var config = await _repository.GetAsync(id);
        if (config.CreatorId != userId || config.IsSystem)
            throw new BusinessException("Aether:LlmConfigNotFound");

        // 检查是否有 Agent 在用
        await CheckLlmConfigInUseAsync(id, userId);

        await _repository.DeleteAsync(config);
    }

    #region 系统级 LLM 配置管理（仅管理员）

    /// <summary>
    /// 获取系统级 LLM 配置（仅管理员）。系统配置在 host（TenantId=null），切 host 上下文操作。
    /// </summary>
    [Authorize(AbpPermissions.SystemLlmConfigs.Default)]
    public async Task<ListResultDto<LlmConfigDto>> GetSystemLlmConfigsAsync()
    {
        using (CurrentTenant.Change(null))
        {
            var queryable = await _repository.GetQueryableAsync();
            var configs = await AsyncExecuter.ToListAsync(
                queryable.Where(c => c.IsSystem)
                    .OrderByDescending(c => c.IsDefault)
                    .ThenBy(c => c.Name)
            );

            return new ListResultDto<LlmConfigDto>(
                configs.Select(MapToDto).ToList()
            );
        }
    }

    /// <summary>
    /// 新增或更新系统级 LLM 配置（仅管理员，强制 IsSystem=true）
    /// </summary>
    [Authorize(AbpPermissions.SystemLlmConfigs.Create)]
    public async Task<LlmConfigDto> SetSystemLlmConfigAsync(LlmConfigCreateDto input)
    {
        using (CurrentTenant.Change(null))  // 系统配置在 host
        {
            if (input.Id.HasValue && input.Id.Value != Guid.Empty)
            {
                // 更新
                var config = await _repository.GetAsync(input.Id.Value);
                if (!config.IsSystem)
                    throw new BusinessException("Aether:LlmConfigNotSystemConfig");

                config.Name = input.Name;
                config.ModelName = input.ModelName;
                config.ApiKey = input.ApiKey;
                config.Endpoint = input.Endpoint;
                config.Temperature = input.Temperature;

                if (input.IsDefault && !config.IsDefault)
                {
                    await ClearOtherDefaultsAsync(null, isSystem: true);
                    config.IsDefault = true;
                }

                await _repository.UpdateAsync(config);
                return MapToDto(config);
            }
            else
            {
                // 新增 — 强制 IsSystem=true
                var isFirst = !await _repository.AnyAsync(c => c.IsSystem);
                var config = new AppLlmConfig(
                    input.Name,
                    input.ModelName,
                    input.ApiKey,
                    input.Endpoint,
                    input.Temperature,
                    input.IsDefault || isFirst,  // 第一条系统配置自动设为系统默认
                    isSystem: true
                );

                if (config.IsDefault)
                    await ClearOtherDefaultsAsync(null, isSystem: true);

                await _repository.InsertAsync(config);
                return MapToDto(config);
            }
        }
    }

    /// <summary>
    /// 设置系统默认 LLM 配置（仅管理员）
    /// </summary>
    [Authorize(AbpPermissions.SystemLlmConfigs.Update)]
    public async Task SetSystemDefaultLlmConfigAsync(Guid id)
    {
        using (CurrentTenant.Change(null))  // 系统配置在 host
        {
            var config = await _repository.GetAsync(id);
            if (!config.IsSystem)
                throw new BusinessException("Aether:LlmConfigNotSystemConfig");

            await ClearOtherDefaultsAsync(null, isSystem: true);
            config.IsDefault = true;
            await _repository.UpdateAsync(config);
        }
    }

    /// <summary>
    /// 删除系统级 LLM 配置（仅管理员，检查全局引用）
    /// </summary>
    [Authorize(AbpPermissions.SystemLlmConfigs.Delete)]
    public async Task DeleteSystemLlmConfigAsync(Guid id)
    {
        using (CurrentTenant.Change(null))  // 系统配置在 host
        {
            var config = await _repository.GetAsync(id);
            if (!config.IsSystem)
                throw new BusinessException("Aether:LlmConfigNotSystemConfig");

            // AppAgentConfig 已不再持有 LlmConfigId（LLM 选择完全本地化），无需检查 Agent 引用

            // 如果删除的是系统默认，提升下一条系统配置为默认
            if (config.IsDefault)
            {
                await PromoteNextDefaultAsync(null, id, isSystem: true);
            }

            await _repository.DeleteAsync(config);
        }
    }

    #endregion

    #region 私有辅助方法

    /// <summary>
    /// 清除同一层级其他默认标记。
    /// isSystem=true: 清除系统级默认；isSystem=false: 清除指定用户的默认。
    /// </summary>
    private async Task ClearOtherDefaultsAsync(Guid? userId, bool isSystem)
    {
        var queryable = await _repository.GetQueryableAsync();
        var query = queryable.Where(c => c.IsSystem == isSystem && c.IsDefault);
        if (!isSystem && userId.HasValue)
            query = query.Where(c => c.CreatorId == userId.Value);

        var defaults = await AsyncExecuter.ToListAsync(query);
        foreach (var d in defaults)
            d.IsDefault = false;
        if (defaults.Count > 0)
            await _repository.UpdateManyAsync(defaults);
    }

    /// <summary>
    /// 检查 LLM 配置是否被 Agent 配置引用。
    /// 注：AppAgentConfig 已不再持有 LlmConfigId（LLM 选择完全本地化），此检查已无意义，保留为空操作供调用方兼容。
    /// </summary>
    private Task CheckLlmConfigInUseAsync(Guid llmConfigId, Guid? userId)
        => Task.CompletedTask;

    /// <summary>
    /// 删除后提升同层级下一条配置为默认
    /// </summary>
    private async Task PromoteNextDefaultAsync(Guid? userId, Guid excludeId, bool isSystem)
    {
        var queryable = await _repository.GetQueryableAsync();
        var query = queryable.Where(c => c.IsSystem == isSystem && c.Id != excludeId);
        if (!isSystem && userId.HasValue)
            query = query.Where(c => c.CreatorId == userId.Value);

        var nextDefault = await AsyncExecuter.FirstOrDefaultAsync(
            query.OrderBy(c => c.CreationTime)
        );
        if (nextDefault != null)
        {
            nextDefault.IsDefault = true;
            await _repository.UpdateAsync(nextDefault);
        }
    }

    private LlmConfigDto MapToDto(AppLlmConfig entity)
    {
        var dto = new LlmConfigDto
        {
            Id = entity.Id,
            Name = entity.Name,
            ModelName = entity.ModelName,
            ApiKey = entity.ApiKey,
            Endpoint = entity.Endpoint,
            Temperature = entity.Temperature,
            IsDefault = entity.IsDefault,
            IsSystem = entity.IsSystem,
            CreatorId = entity.CreatorId,
        };

        if (entity.CreatorId.HasValue)
        {
            var user = _userManager.FindByIdAsync(entity.CreatorId.Value.ToString()).Result;
            dto.CreatorUserName = user?.UserName;
        }

        return dto;
    }

    #endregion
}
