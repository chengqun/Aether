using Localization.Resources.AbpUi;
using Aether.Server.Application.Contracts.Services.Chat;
using Aether.Server.HttpApi.Services;
using Aether.Server.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Account;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;
using Volo.Abp.AspNetCore.Mvc;

namespace Aether.Server;

[DependsOn(
    typeof(AbpApplicationModule),
    typeof(AbpApplicationContractsModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpIdentityHttpApiModule),
    typeof(AbpPermissionManagementHttpApiModule),
    typeof(AbpTenantManagementHttpApiModule),
    typeof(AbpFeatureManagementHttpApiModule),
    typeof(AbpSettingManagementHttpApiModule)
    )]
public class AbpHttpApiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        ConfigureLocalization();
        ConfigureAutoApiControllers();

        context.Services.AddSingleton<IChatNotifier, SignalRChatNotifier>();
        // 设备 SignalR 连接追踪：设备经 authorizationCode 连接（无 sub claim，Clients.User 触达不了），
        // 需主动追踪连接以便服务端把已连接设备拉进会话组。
        context.Services.AddSingleton<IDeviceConnectionTracker, DeviceConnectionTracker>();
    }
    
    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers
                .Create(typeof(AbpApplicationModule).Assembly);
        });
    }

    private void ConfigureLocalization()
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Get<AbpResource>()
                .AddBaseTypes(
                    typeof(AbpUiResource)
                );
        });
    }
}
