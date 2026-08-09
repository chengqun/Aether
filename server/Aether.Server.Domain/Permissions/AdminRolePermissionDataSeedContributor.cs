using System;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Uow;

namespace Aether.Server.Permissions;

/// <summary>
/// 给当前上下文（host 或某租户）的 admin 角色授予全部适用权限，使其登录 Blazor 能看到管理页。
/// host 上下文授 Host 侧权限，租户上下文授 Tenant 侧权限（按 PermissionDefinition.MultiTenancySide 过滤，
/// 避免把 host 专属权限漏授给租户管理员）。幂等——IPermissionDataSeeder 仅补未授权项，不覆盖手改。
/// 创建新租户时 ABP 会以租户上下文重跑本 seeder，自动给新租户 admin 角色授权。
/// MultiTenancySide 为 [Flags]：Host=1, Tenant=2, Both=3，按位与判断适用侧。
/// </summary>
public class AdminRolePermissionDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IPermissionDefinitionManager _permissionDefinitionManager;
    private readonly IPermissionDataSeeder _permissionDataSeeder;

    public AdminRolePermissionDataSeedContributor(
        IPermissionDefinitionManager permissionDefinitionManager,
        IPermissionDataSeeder permissionDataSeeder)
    {
        _permissionDefinitionManager = permissionDefinitionManager;
        _permissionDataSeeder = permissionDataSeeder;
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        // Host=1, Tenant=2 —— 租户上下文只授 Tenant 侧，host 上下文只授 Host 侧
        var sideFlag = context.TenantId.HasValue ? 2 : 1;

        var permissionNames = (await _permissionDefinitionManager.GetPermissionsAsync())
            .Where(p => (Convert.ToInt32(p.MultiTenancySide) & sideFlag) != 0)
            .Select(p => p.Name)
            .ToList();

        if (permissionNames.Count == 0)
        {
            return;
        }

        await _permissionDataSeeder.SeedAsync(
            "R", // RolePermissionValueProvider.ProviderName —— 按 admin 角色名授权
            "admin",
            permissionNames,
            context.TenantId);
    }
}

