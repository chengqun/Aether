using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aether.Server.MultiTenancy;
using Aether.Server.Domain;
using Aether.Server.Domain.Entities;
using Volo.Abp.AuditLogging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Emailing;
using Volo.Abp.EventBus;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.OpenIddict;
using Volo.Abp.PermissionManagement.Identity;
using Volo.Abp.PermissionManagement.OpenIddict;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;

namespace Aether.Server;

[DependsOn(
    typeof(AbpDomainSharedModule),
    typeof(AbpAuditLoggingDomainModule),
    typeof(AbpBackgroundJobsDomainModule),
    typeof(AbpEventBusModule),
    typeof(AbpFeatureManagementDomainModule),
    typeof(AbpIdentityDomainModule),
    typeof(AbpOpenIddictDomainModule),
    typeof(AbpPermissionManagementDomainOpenIddictModule),
    typeof(AbpPermissionManagementDomainIdentityModule),
    typeof(AbpSettingManagementDomainModule),
    typeof(AbpTenantManagementDomainModule),
    typeof(AbpEmailingModule)
)]
public class AbpDomainModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Languages.Add(new LanguageInfo("ar", "ar", "العربية"));
            options.Languages.Add(new LanguageInfo("cs", "cs", "Čeština"));
            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("en-GB", "en-GB", "English (UK)"));
            options.Languages.Add(new LanguageInfo("hu", "hu", "Magyar"));
            options.Languages.Add(new LanguageInfo("hr", "hr", "Croatian"));
            options.Languages.Add(new LanguageInfo("fi", "fi", "Finnish"));
            options.Languages.Add(new LanguageInfo("fr", "fr", "Français"));
            options.Languages.Add(new LanguageInfo("hi", "hi", "Hindi"));
            options.Languages.Add(new LanguageInfo("it", "it", "Italiano"));
            options.Languages.Add(new LanguageInfo("pt-BR", "pt-BR", "Português"));
            options.Languages.Add(new LanguageInfo("ru", "ru", "Русский"));
            options.Languages.Add(new LanguageInfo("sk", "sk", "Slovak"));
            options.Languages.Add(new LanguageInfo("tr", "tr", "Türkçe"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "简体中文"));
            options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "繁體中文"));
            options.Languages.Add(new LanguageInfo("de-DE", "de-DE", "Deutsch"));
            options.Languages.Add(new LanguageInfo("es", "es", "Español"));
        });

        Configure<AbpMultiTenancyOptions>(options =>
        {
            options.IsEnabled = MultiTenancyConsts.IsEnabled;
        });

        // 注册设备管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Devices.IDeviceManager,
            Aether.Server.Domain.Services.Devices.DeviceManager>();

        // 注册群组管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Groups.IGroupManager,
            Aether.Server.Domain.Services.Groups.GroupManager>();

        // 注册关系管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Relationships.IRelationshipManager<Aether.Server.Domain.Entities.AppRelationship, Volo.Abp.Identity.IdentityUser, System.Guid>,
            Aether.Server.Domain.Services.Relationships.FriendshipManager>();
        context.Services.AddTransient<Aether.Server.Domain.Services.Relationships.IRelationshipManager<Aether.Server.Domain.Entities.AppRelationship, Aether.Server.Domain.Entities.AppDevice, System.Guid>,
            Aether.Server.Domain.Services.Relationships.DeviceRelationshipManager>();
        context.Services.AddTransient<Aether.Server.Domain.Services.Relationships.IRelationshipManager<Aether.Server.Domain.Entities.AppRelationship, Aether.Server.Domain.Entities.AppGroup, System.Guid>,
            Aether.Server.Domain.Services.Relationships.GroupRelationshipManager>();
        context.Services.AddTransient<Aether.Server.Domain.Services.Relationships.IRelationshipManager<Aether.Server.Domain.Entities.AppRelationship, Aether.Server.Domain.Entities.AppSecurity, System.Guid>,
            Aether.Server.Domain.Services.Relationships.StockRelationshipManager>();
        context.Services.AddTransient<Aether.Server.Domain.Services.Relationships.IRelationshipManager<Aether.Server.Domain.Entities.AppRelationship, Aether.Server.Domain.Entities.AppServiceAccount, System.Guid>,
            Aether.Server.Domain.Services.Relationships.ServiceAccountRelationshipManager>();

        // 注册服务号管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.ServiceAccounts.IServiceAccountManager,
            Aether.Server.Domain.Services.ServiceAccounts.ServiceAccountManager>();
        context.Services.AddTransient<Aether.Server.Domain.Services.ServiceAccounts.IServiceAccountPostManager,
            Aether.Server.Domain.Services.ServiceAccounts.ServiceAccountPostManager>();
        
        // 注册证券管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Securities.ISecurityManager,
            Aether.Server.Domain.Services.Securities.SecurityManager>();

        // 注册动态管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Moment.IMomentManager,
            Aether.Server.Domain.Services.Moment.MomentManager>();

        // 注册会话管理服务
        context.Services.AddTransient<Aether.Server.Domain.Services.Chat.IConversationManager,
            Aether.Server.Domain.Services.Chat.ConversationManager>();

#if DEBUG
        context.Services.Replace(ServiceDescriptor.Singleton<IEmailSender, NullEmailSender>());
#endif
    }
}