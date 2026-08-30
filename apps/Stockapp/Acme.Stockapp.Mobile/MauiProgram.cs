using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Mvvm.ViewModels;
using Acme.Mobile.Shared.Hosting;
using Acme.Mobile.Shared.Views;
using Acme.Quotes.AI;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Infrastructure.DependencyInjection;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace Acme.Stockapp.Mobile;

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
        var tenantDir = Path.Combine(FileSystem.AppDataDirectory, "Aether-Stockapp");

        // 公共 DI 组合（AppAuth/Llm/Notes/ChatSocial + Aether 运行时 + 手机平台服务 + Securities 已注册）。
        // AddMobileCommon 返回 AetherAIBuilder，供租户链式追加专属 AI 工具。
        var aether = builder.Services.AddMobileCommon("stockapp", tenantDir);

        // —— Stockapp 专属：Quotes 行情上下文（仅 agent 工具，无行情 UI 页）——
        // Securities 已在 AddMobileCommon 内注册（AddSecurityApplication + AddSecurityInfrastructure）。
        // dbPath 必须显式传——AddQuotesInfrastructure 默认 ~/.aether/quotes.db 在 mobile 沙盒外无意义。
        builder.Services.AddQuotesInfrastructure(Path.Combine(tenantDir, "quotes.db"));
        builder.Services.AddQuotesApplication();
        aether.AddQuotesTools();  // QuerySecurity + CalculateIndicators（agent 工具，非 UI 页）

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
