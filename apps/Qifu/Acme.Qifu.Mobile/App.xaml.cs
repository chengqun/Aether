using Acme.Mobile.Shared.Services;
using Acme.Mobile.Shared.Views;

namespace Acme.Qifu.Mobile;

// 全限定 Application 基类：本项目新引 Acme.Qifu.Application 命名空间（业务层），它处于当前命名空间
// Acme.Qifu.Mobile 的外层 Acme.Qifu 下，C# 简名解析优先查外层命名空间成员 → "Application" 被解析成命名空间（CS0118）。
// using-alias 无效（外层命名空间成员优先于文件级 alias），只能全限定到 global::。
public partial class App : global::Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
        InitializeMarkdownTheme();

        // 主题：默认跟随系统（Mobile.Shared 的 MauiThemeManager 在 Splash 初始化时应用保存的偏好）
        UserAppTheme = AppTheme.Unspecified;
    }

    private void InitializeMarkdownTheme()
    {
        // Markdown 主题：light/dark 双 palette（读 Colors.xaml token），供 MarkdownView 引用。
        // MarkdownView 需设 UseAppTheme="True" 随 UserAppTheme 自动切换。
        // 传入 this.Resources：App ctor 期 Application.Current 未就绪，显式传已合并 Colors.xaml 的资源表。
        Resources[MobileMarkdownTheme.ResourceKey] = MobileMarkdownTheme.Build(Resources);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // 路由注册（Shell 子页面：agent 详情 + Stage 2 社交子页）。
        // LoginPage 是 AppShell.xaml 根 ShellContent，//LoginPage 走 ShellContent，无需 RegisterRoute。
        Routing.RegisterRoute("AgentContactDetailPage", typeof(AgentContactDetailPage));
        Routing.RegisterRoute("AgentChatDetailPage", typeof(AgentChatDetailPage));
        Routing.RegisterRoute("AgentPickerPage", typeof(AgentPickerPage));
        Routing.RegisterRoute("ChatDetailPage", typeof(ChatDetailPage));
        Routing.RegisterRoute("DesktopAssistantPage", typeof(DesktopAssistantPage));
        Routing.RegisterRoute("ContactDetailPage", typeof(ContactDetailPage));
        Routing.RegisterRoute("ContactMomentsPage", typeof(ContactMomentsPage));
        Routing.RegisterRoute("MomentsPage", typeof(MomentsPage));
        Routing.RegisterRoute("AddFriendPage", typeof(AddFriendPage));
        Routing.RegisterRoute("NewFriendsPage", typeof(NewFriendsPage));
        Routing.RegisterRoute("CreateGroupPage", typeof(CreateGroupPage));
        Routing.RegisterRoute("GroupDetailPage", typeof(GroupDetailPage));
        Routing.RegisterRoute("ServiceAccountDetailPage", typeof(ServiceAccountDetailPage));
        Routing.RegisterRoute("ServiceAccountFeedPage", typeof(ServiceAccountFeedPage));
        Routing.RegisterRoute("ServiceAccountPostDetailPage", typeof(ServiceAccountPostDetailPage));
        // Stage 2b 次要子页
        Routing.RegisterRoute("VideoChannelsPage", typeof(VideoChannelsPage));
        Routing.RegisterRoute("DeviceManagementPage", typeof(DeviceManagementPage));
        Routing.RegisterRoute("LlmConfigPage", typeof(LlmConfigPage));
        Routing.RegisterRoute("AiSettingsPage", typeof(AiSettingsPage));
        Routing.RegisterRoute("SettingsPage", typeof(SettingsPage));
        Routing.RegisterRoute("AssetListPage", typeof(AssetListPage));
        Routing.RegisterRoute("AgentEditPage", typeof(AgentEditPage));


        Routing.RegisterRoute("SkillCreatePage", typeof(SkillCreatePage));
        Routing.RegisterRoute("McpCreatePage", typeof(McpCreatePage));
        Routing.RegisterRoute("WorkflowCreatePage", typeof(WorkflowCreatePage));
        Routing.RegisterRoute("KnowledgeCreatePage", typeof(KnowledgeCreatePage));

        // 连接器页（毓数/云文档/mcphub/MySQL）+ MySQL 配置子页
        Routing.RegisterRoute("ConnectorPage", typeof(Acme.Mobile.Shared.Views.ConnectorPage));
        Routing.RegisterRoute("MySqlSettingsPage", typeof(Acme.Mobile.Shared.Views.MySqlSettingsPage));

        return new Window(new AppShell());
    }
}
