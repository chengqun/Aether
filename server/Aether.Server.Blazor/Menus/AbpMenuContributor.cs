using System.Threading.Tasks;
using Aether.Server.Localization;
using Aether.Server.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Features;
using Volo.Abp.Identity.Blazor;
using Volo.Abp.MultiTenancy;
using Volo.Abp.SettingManagement.Blazor.Menus;
using Volo.Abp.TenantManagement.Blazor.Navigation;
using Volo.Abp.UI.Navigation;

namespace Aether.Server.Blazor.Menus;

public class AbpMenuContributor : IMenuContributor
{
    public async Task ConfigureMenuAsync(MenuConfigurationContext context)
    {
        if (context.Menu.Name == StandardMenus.Main)
        {
            await ConfigureMainMenuAsync(context);
        }
    }

    private async Task ConfigureMainMenuAsync(MenuConfigurationContext context)
    {
        var administration = context.Menu.GetAdministration();
        var l = context.GetLocalizer<AbpResource>();

        // 添加用户管理菜单
        var userManagement = context.Menu.AddItem(new ApplicationMenuItem(
            "UserManagement",
            l["UserManagement"],
            icon: "fas fa-user-cog"
        ));

        // 朋友圈页面（按租户功能 Chat.Moment 显隐；host 始终显示）
        if (await IsFeatureVisibleAsync(context, "Chat.Moment"))
        {
            context.Menu.AddItem(new ApplicationMenuItem(
                AbpMenus.Moments,
                "朋友圈",
                url: "/moments",
                icon: "fas fa-image"
            ));
        }

        // 管理员菜单 - 股票信息管理（按租户功能 StockApp.Quote 显隐；host 始终显示）
        if (await context.IsGrantedAsync("Abp.Securities.Manage")
            && await IsFeatureVisibleAsync(context, "StockApp.Quote"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "SecurityManagement",
                l["SecurityManagement"],
                url: "/security-management",
                icon: "fas fa-shield-alt"
            ));
        }

        // 管理员菜单 - 应用版本管理
        if (await context.IsGrantedAsync("Abp.VersionConfigs.Manage"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "VersionConfigManagement",
                l["VersionConfigManagement"],
                url: "/version-config-management",
                icon: "fas fa-mobile-alt"
            ));
        }

        // 管理员菜单 - Agent配置管理（市场）
        if (await context.IsGrantedAsync("Abp.AgentConfigs.Manage"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "AgentConfigManagement",
                l["AgentConfigManagement"],
                url: "/agent-config-management",
                icon: "fas fa-robot"
            ));
        }

        // 管理员菜单 - LLM 配置管理
        if (await context.IsGrantedAsync("Abp.LlmConfigs.Manage"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "LlmConfigManagement",
                l["LlmConfigManagement"],
                url: "/llm-config-management",
                icon: "fas fa-microchip"
            ));
        }

        // 管理员菜单 - 服务号管理（按租户功能 Chat.ServiceAccount 显隐；host 始终显示）
        if (await context.IsGrantedAsync("Abp.ServiceAccounts.Manage")
            && await IsFeatureVisibleAsync(context, "Chat.ServiceAccount"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "ServiceAccountManagement",
                l["ServiceAccountManagement"],
                url: "/service-account-management",
                icon: "fas fa-bullhorn"
            ));
        }

        // 管理员菜单 - Agent 使用统计
        if (await context.IsGrantedAsync("Abp.AgentUsage.View"))
        {
            administration.AddItem(new ApplicationMenuItem(
                "AgentUsageManagement",
                l["AgentUsageManagement"],
                url: "/agent-usage",
                icon: "fas fa-chart-bar"
            ));
        }

        if (MultiTenancyConsts.IsEnabled)
        {
            administration.SetSubItemOrder(TenantManagementMenuNames.GroupName, 1);
        }
        else
        {
            administration.TryRemoveMenuItem(TenantManagementMenuNames.GroupName);
        }

        administration.SetSubItemOrder(IdentityMenuNames.GroupName, 2);
        administration.SetSubItemOrder(SettingManagementMenus.GroupName, 3);
    }

    /// <summary>
    /// 菜单按租户功能显隐：host 管理员（无租户上下文）跨租户管理，始终返回 true；
    /// 租户用户按当前租户的 feature 值判断（feature 未设置时回退到定义默认值）。
    /// </summary>
    private static async Task<bool> IsFeatureVisibleAsync(MenuConfigurationContext context, string featureName)
    {
        var currentTenant = context.ServiceProvider.GetRequiredService<ICurrentTenant>();
        if (currentTenant.Id == null)
        {
            return true; // host 管理员始终可见
        }

        var featureChecker = context.ServiceProvider.GetRequiredService<IFeatureChecker>();
        return await featureChecker.IsEnabledAsync(featureName);
    }
}