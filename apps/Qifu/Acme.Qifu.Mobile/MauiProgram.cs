using Acme.DataQuery.AI;
using Acme.DataQuery.Application;
using Acme.DataQuery.Infrastructure;
using Acme.Mobile.Mvvm.Services;
using Acme.Qifu.AI;
using Acme.Mobile.Mvvm.ViewModels;
using Acme.Mobile.Shared.Hosting;
using Acme.Mobile.Shared.Views;
using Acme.Qifu.Mobile.Hosting;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace Acme.Qifu.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
               .UseMauiCommunityToolkit()
               .UseMauiCommunityToolkitMediaElement(false)
               .ConfigureFonts(fonts =>
               {
                   fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                   fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
               });

        // 租户数据目录（沙盒内，与桌面隔离策略一致：各上下文 SQLite 落此）
        var tenantDir = Path.Combine(FileSystem.AppDataDirectory, "Aether-Qifu");

        // 公共 DI 组合（AppAuth/Llm/Notes/ChatSocial + Aether 运行时 + 手机平台服务 + Stage-2 桩）
        var aether = builder.Services.AddMobileCommon("qifu", tenantDir);

        // 毓数/云文档 移动端适配器（IQifuConfigCache + 业务 token 同步 + 4 个手机 VM 层端口适配器）。
        // 不引 Qifu.Infrastructure（Playwright）；AI 设置页毓数/云文档卡片走"从服务器同步 token"路径。
        builder.Services.AddQifuMobileServices(tenantDir);

        // —— MySQL 直连数据查询（ExecuteSql 工具，Dangerous）——
        // AddDataQueryInfrastructure 内部 AddSingleton<DataQueryOptions>()（空实例）；随后覆盖为从
        // 手机沙盒 preferences.json 读 mysql.* 键（与桌面连接器卡同键名，复用 IAppSettings）的实例（DI 后注册 win）。
        builder.Services.AddDataQueryInfrastructure();
        builder.Services.AddDataQueryApplication();
        builder.Services.AddSingleton<Acme.DataQuery.Domain.DataQueryOptions>(sp =>
        {
            var settings = sp.GetRequiredService<IAppSettings>();
            var opts = new Acme.DataQuery.Domain.DataQueryOptions
            {
                Host = settings.GetString("mysql.host", string.Empty),
                User = settings.GetString("mysql.user", string.Empty),
                Password = settings.GetString("mysql.password", string.Empty),
                Database = settings.GetString("mysql.database", string.Empty),
            };
            if (int.TryParse(settings.GetString("mysql.port", "3306"), out var port)) opts.Port = port;
            return opts;
        });
        aether.AddDataQueryTools();

        // —— 企服工具（双端通用：毓数 SQL / 权限工单 / Teams）——
        // 毓数 SQL + 权限工单经 mcphub 网关（IYushuService→YushuService 纯 HTTP + MobileMcphubAuthService 拉服务器 JWT，
        // 已在 AddQifuMobileServices 注册）。云文档浏览器类工具走 AddQifuCloudDocsTools（桌面-only），手机不注册。
        aether.AddQifuTools();

        // —— 连接器页（毓数/云文档/mcphub/MySQL 数据驱动卡片）——
        // 卡 Singleton（认证态/MySQL 字段跨页面访问需留存）；ConnectorVM Transient（取 IEnumerable<ConnectorCardViewModel> 解析）；
        // 子页 Page Transient（照 AiSettingsPage/LlmConfigPage）。3 张 auth 卡由 QifuMobileServices 的 auth 适配器桥接。
        //
        // 关键：必须以【基类 ConnectorCardViewModel 为服务类型】注册——MSDI 的 IEnumerable<T> 只收录服务类型恰好是 T 的注册，
        // 不扫可分配子类。若只 AddSingleton<YushuConnectorCard>()（服务类型=具体类），ConnectorViewModel 解析的集合为空→连接器页无卡。
        // auth 卡只经 IEnumerable 消费，直接以基类注册即可；MySQL 卡需 MySqlSettingsPage 注入具体类型 + 连接器页经 IEnumerable，
        // 故【具体类 Singleton + 工厂转发到基类同一实例】保证两处拿到同一单例（共享认证态/字段）。
        builder.Services.AddSingleton<Acme.Mobile.Mvvm.ViewModels.Connector.ConnectorCardViewModel, Acme.Mobile.Mvvm.ViewModels.Connector.YushuConnectorCard>();
        builder.Services.AddSingleton<Acme.Mobile.Mvvm.ViewModels.Connector.ConnectorCardViewModel, Acme.Mobile.Mvvm.ViewModels.Connector.CloudDocsConnectorCard>();
        builder.Services.AddSingleton<Acme.Mobile.Mvvm.ViewModels.Connector.ConnectorCardViewModel, Acme.Mobile.Mvvm.ViewModels.Connector.McphubConnectorCard>();
        builder.Services.AddSingleton<Acme.Mobile.Mvvm.ViewModels.Connector.MySqlConnectorCard>();
        builder.Services.AddSingleton<Acme.Mobile.Mvvm.ViewModels.Connector.ConnectorCardViewModel>(sp => sp.GetRequiredService<Acme.Mobile.Mvvm.ViewModels.Connector.MySqlConnectorCard>());
        builder.Services.AddTransient<Acme.Mobile.Mvvm.ViewModels.Connector.ConnectorViewModel>();
        builder.Services.AddTransient<Acme.Mobile.Shared.Views.ConnectorPage>();
        builder.Services.AddTransient<Acme.Mobile.Shared.Views.MySqlSettingsPage>();

        // —— ViewModel 注册（TabBar 主页 Singleton 保持状态；子页面 Transient 避免串扰）——
        builder.Services.AddSingleton<SplashViewModel>();
        // Tab VM（Singleton，保持会话列表/发现/我的状态）
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<AiChatViewModel>();
        builder.Services.AddSingleton<AiChatSurfaceViewModel>();   // AI tab 常驻聊天面板（Singleton）
        builder.Services.AddSingleton<ContactsViewModel>();
        builder.Services.AddSingleton<DiscoveryViewModel>();
        builder.Services.AddSingleton<MeViewModel>();
        // 子页 VM（Transient，每次导航新建，避免串扰）
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<AgentContactDetailViewModel>();
        builder.Services.AddTransient<AgentChatDetailViewModel>();
        builder.Services.AddTransient<AgentPickerViewModel>();
        builder.Services.AddTransient<ChatDetailViewModel>();
        builder.Services.AddTransient<DesktopAssistantViewModel>();
        builder.Services.AddTransient<ContactDetailViewModel>();
        builder.Services.AddTransient<ContactMomentsViewModel>();
        builder.Services.AddTransient<MomentsViewModel>();
        builder.Services.AddTransient<AddFriendViewModel>();
        builder.Services.AddTransient<NewFriendsViewModel>();
        builder.Services.AddTransient<CreateGroupViewModel>();
        builder.Services.AddTransient<GroupDetailViewModel>();
        builder.Services.AddTransient<ServiceAccountDetailViewModel>();
        builder.Services.AddTransient<ServiceAccountFeedViewModel>();
        builder.Services.AddTransient<ServiceAccountPostDetailViewModel>();
        // Stage 2b 次要子页 VM
        builder.Services.AddTransient<VideoChannelsViewModel>();
        builder.Services.AddTransient<DeviceManagementViewModel>();
        builder.Services.AddTransient<LlmConfigViewModel>();
        builder.Services.AddTransient<AiSettingsViewModel>();
        builder.Services.AddTransient<AgentEditViewModel>();

        builder.Services.AddTransient<SkillCreateViewModel>();
        builder.Services.AddTransient<McpCreateViewModel>();
        builder.Services.AddTransient<WorkflowCreateViewModel>();
        builder.Services.AddTransient<KnowledgeCreateViewModel>();

        // —— Page 注册（Shell 导航按路由解析，页面 ctor 注入 VM，必须注册）——
        builder.Services.AddSingleton<SplashPage>();
        // Tab Page（Singleton，与 Tab VM 同生命周期）
        builder.Services.AddSingleton<ChatPage>();
        builder.Services.AddSingleton<AiChatPage>();
        builder.Services.AddSingleton<ContactsPage>();
        builder.Services.AddSingleton<DiscoveryPage>();
        builder.Services.AddSingleton<MePage>();
        // 子页 Page（Transient）
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<AgentContactDetailPage>();
        builder.Services.AddTransient<AgentChatDetailPage>();
        builder.Services.AddTransient<AgentPickerPage>();
        builder.Services.AddTransient<ChatDetailPage>();
        builder.Services.AddTransient<DesktopAssistantPage>();
        builder.Services.AddTransient<ContactDetailPage>();
        builder.Services.AddTransient<ContactMomentsPage>();
        builder.Services.AddTransient<MomentsPage>();
        builder.Services.AddTransient<AddFriendPage>();
        builder.Services.AddTransient<NewFriendsPage>();
        builder.Services.AddTransient<CreateGroupPage>();
        builder.Services.AddTransient<GroupDetailPage>();
        builder.Services.AddTransient<ServiceAccountDetailPage>();
        builder.Services.AddTransient<ServiceAccountFeedPage>();
        builder.Services.AddTransient<ServiceAccountPostDetailPage>();
        // Stage 2b 次要子页 Page
        builder.Services.AddTransient<VideoChannelsPage>();
        builder.Services.AddTransient<DeviceManagementPage>();
        builder.Services.AddTransient<LlmConfigPage>();
        builder.Services.AddTransient<AiSettingsPage>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AssetListViewModel>();
        builder.Services.AddTransient<AssetListPage>();
        builder.Services.AddTransient<AgentEditPage>();


        builder.Services.AddTransient<SkillCreatePage>();
        builder.Services.AddTransient<McpCreatePage>();
        builder.Services.AddTransient<WorkflowCreatePage>();
        builder.Services.AddTransient<KnowledgeCreatePage>();

        // AiSettingsViewModel 静态 IAppSettings 引用（供 GetStockModeAgentName 快速返回）
        var appSettings = new Acme.Mobile.Shared.Services.MauiAppSettings(tenantDir);
        AiSettingsViewModel.InitializeStatic(appSettings);

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
