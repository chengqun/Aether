using Acme.Mobile.Shared.Views;
using Indiko.Maui.Controls.Markdown.Theming;

namespace Acme.Stockapp.Mobile;

public partial class App : Application
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
        // Markdown 渲染主题（GitHub 风），供 AgentChatDetailPage 的 MarkdownView 引用
        var mdTheme = MarkdownThemeDefaults.GitHub.Clone();
        Resources["AppMarkdownTheme"] = mdTheme;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // 路由注册（Shell 子页面：登录 + agent 详情 + Stage 2 社交子页）
        Routing.RegisterRoute("LoginPage", typeof(LoginPage));
        Routing.RegisterRoute("AgentContactDetailPage", typeof(AgentContactDetailPage));
        Routing.RegisterRoute("AgentChatDetailPage", typeof(AgentChatDetailPage));
        Routing.RegisterRoute("ChatDetailPage", typeof(ChatDetailPage));
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
        Routing.RegisterRoute("MarketPage", typeof(MarketPage));
        Routing.RegisterRoute("DeviceManagementPage", typeof(DeviceManagementPage));
        Routing.RegisterRoute("LlmConfigPage", typeof(LlmConfigPage));
        Routing.RegisterRoute("AiSettingsPage", typeof(AiSettingsPage));

        return new Window(new AppShell());
    }
}
