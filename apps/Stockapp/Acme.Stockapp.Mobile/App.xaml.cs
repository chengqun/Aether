using Acme.Mobile.Shared.Services;
using Acme.Mobile.Shared.Views;

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
        // Markdown 主题：light/dark 双 palette（映射 app token），供 MarkdownView 引用。
        // MarkdownView 需设 UseAppTheme="True" 随 UserAppTheme 自动切换。
        Resources["AppMarkdownTheme"] = MobileMarkdownTheme.Build();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // 路由注册（Shell 子页面：agent 详情 + Stage 2 社交子页）。
        // LoginPage 是 AppShell.xaml 根 ShellContent，//LoginPage 走 ShellContent，无需 RegisterRoute。
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
