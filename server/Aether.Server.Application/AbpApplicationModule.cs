using Microsoft.Extensions.Configuration;
using Volo.Abp.Account;
using Volo.Abp.Mapperly;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;
using Microsoft.Extensions.DependencyInjection;
using Aether.Server.Application.Contracts.Services.Security;

namespace Aether.Server;

[DependsOn(
    typeof(AbpDomainModule),
    typeof(AbpAccountApplicationModule),
    typeof(AbpApplicationContractsModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpTenantManagementApplicationModule),
    typeof(AbpFeatureManagementApplicationModule),
    typeof(AbpSettingManagementApplicationModule)
    )]
public class AbpApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var connectionString = configuration.GetConnectionString("Default");

        context.Services.AddMapperlyObjectMapper<AbpApplicationModule>();

        // 注册应用服务
        context.Services.AddTransient<Aether.Server.Application.Contracts.Services.Relationships.IRelationshipService,
            Aether.Server.Application.Services.Relationships.RelationshipService>();
        context.Services.AddTransient<Aether.Server.Application.Contracts.Services.Security.ISecurityAppService,
            Aether.Server.Application.Services.Security.SecurityAppService>();
        context.Services.AddTransient<Aether.Server.Application.Contracts.Services.Devices.IDeviceAppService,
            Aether.Server.Application.Services.Devices.DeviceAppService>();
        context.Services.AddTransient<Aether.Server.Application.Contracts.Services.Chat.IChatAppService,
            Aether.Server.Application.Services.Chat.ChatAppService>();
        context.Services.AddTransient<Aether.Server.Application.Contracts.Services.VersionConfig.IVersionConfigAppService,
            Aether.Server.Application.Services.VersionConfig.VersionConfigAppService>();
    }
}
