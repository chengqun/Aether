using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.PermissionManagement;

namespace Aether.Server.Features;

/// <summary>
/// 给 host 的 admin 角色授予 FeatureManagement 权限，使其能在租户管理页对每个租户
/// 勾选/关闭功能模块（Features 按钮）以及管理 host 级特性。仅在 host 上下文运行（幂等）。
/// </summary>
public class FeatureManagementPermissionDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IPermissionDataSeeder _permissionDataSeeder;

    public FeatureManagementPermissionDataSeedContributor(IPermissionDataSeeder permissionDataSeeder)
    {
        _permissionDataSeeder = permissionDataSeeder;
    }

    public Task SeedAsync(DataSeedContext context)
    {
        // 仅 host：租户级 admin 不应能自行改特性（特性的意义就是 host 控制租户能力）。
        if (context.TenantId.HasValue)
        {
            return Task.CompletedTask;
        }

        var permissions = new List<string>
        {
            "FeatureManagement.Enable",             // 为租户开启特性（租户管理页 Features 按钮）
            "FeatureManagement.Disable",           // 为租户关闭特性
            "FeatureManagement.ManageHostFeatures" // 管理 host 级特性
        };

        return _permissionDataSeeder.SeedAsync(
            "R", // RolePermissionValueProvider.ProviderName —— 授予 admin 角色
            "admin",
            permissions,
            tenantId: null
        );
    }
}
