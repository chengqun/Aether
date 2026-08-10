using System.Collections.ObjectModel;
using System.Windows.Input;
using Acme.Agents.AI;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain.Interfaces;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Notes.Domain.Agents;
using Aether.AI.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 桌面 shell 主 ViewModel 基类：可收起侧边栏 + 导航 + 账户/登录态（Codex/ChatGPT 风）。
/// 租户子类在构造里 Populate <see cref="NavItems"/>。内容区使用 ChatView。
/// 聊天引擎已抽出到 <see cref="ChatWorkspaceViewModel"/>（<see cref="Chat"/>），本类仅持有 shell/nav/账号 +
/// 同级业务 VM（<see cref="IShellHost"/>）。ChatView 绑定 <c>{Binding Chat}</c>，不经本类透传聊天属性。
/// </summary>
public abstract partial class MainViewModelBase : ObservableObject, IShellHost
{
    private readonly IAppAuthService _auth;
    private readonly ICurrentUser _currentUser;
    private readonly ILoginDialogService _loginDialog;
    private readonly IAppTokenStore _tokenStore;
    private readonly ISettingsDialogService _settingsDialog;
    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _dispatcher;

    protected MainViewModelBase(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService, string tenant)
    {
        _auth = auth;
        _currentUser = currentUser;
        _loginDialog = loginDialog;
        _tokenStore = tokenStore;
        _settingsDialog = settingsDialog;
        _serviceProvider = serviceProvider;
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        Trace = serviceProvider.GetService<TraceViewModel>();
        Tenant = tenant;

        var agentRunner = serviceProvider.GetRequiredService<ICustomAgentRunner>();
        var workflowRunner = serviceProvider.GetRequiredService<ICustomWorkflowRunner>();

        Notes = new NotesViewModel(serviceProvider, _dispatcher);
        Automation = new AutomationViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            serviceProvider.GetRequiredService<IAutomationService>(),
            serviceProvider.GetRequiredService<IAutomationScheduler>(),
            _dispatcher);
        Workflows = new WorkflowsViewModel(
            serviceProvider.GetRequiredService<ICustomWorkflowCatalog>(),
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            workflowRunner,
            _dispatcher);
        Workflows.WorkflowsChanged += async () => await Notes.LoadAsync();
        Rules = new RulesViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<McpViewModel>());
        Rules.RulesChanged += async () => await Notes.LoadAsync();
        // Notes 的增删改会影响同源的 Experts（agent 笔记）、Workflows（workflow 笔记）与 Rules（规则笔记），需双向刷新。
        Notes.NotesChanged += async () =>
        {
            await Experts.LoadAsync();
            await Workflows.LoadAsync();
            await Rules.LoadAsync();
        };

        Experts = new ExpertsViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<IAgentMarketplaceService>(),
            _auth,
            Workflows,
            Rules,
            serviceProvider.GetRequiredService<IAgentSourceParser>(),
            serviceProvider.GetRequiredService<McpViewModel>(),
            serviceProvider.GetService<IConnectorViewModel>());

        // 聊天引擎组合：须在 Experts/Workflows/Rules 建好后构造——它订阅三者的 SummonChanged 事件
        //（召唤入池/出池 → 刷新统一下拉池 + 持久化），并在 InitializeAsync 里读这三类列表恢复召唤/选中态。
        Chat = new ChatWorkspaceViewModel(serviceProvider, deviceChatClient, llmService, settingsDialog, Experts, Workflows, Rules);

        // 定时任务执行完成（成功/失败）后刷新「最近会话」——因为 executor 把运行落库进了会话存储。
        serviceProvider.GetRequiredService<IAutomationScheduler>().TaskCompleted +=
            _ => _dispatcher.Post(async () => await Chat.RefreshConversationsAsync());
    }

    public string Tenant { get; }

    /// <summary>运行 Trace 查看 VM（桌面端注册；null = 无 trace 查看，如 sample console）。</summary>
    public TraceViewModel? Trace { get; }

    /// <summary>聊天工作台 VM（流式/转写/持久化/会话/目标/模式/计划/审批）。ChatView 绑定此实例。</summary>
    public ChatWorkspaceViewModel Chat { get; }

    /// <summary>由 shell 在设备连接变更后调用（SettingsViewModel/ISettingsDialogService 经 DI 解析本类调用）。
    /// 透传到 <see cref="Chat"/>。</summary>
    public void RefreshConnectionState() => Chat.RefreshConnectionState();

    /// <summary>LLM 配置变更后刷新聊天页模型下拉（设置页新增/编辑/删除后经 DI 解析本类调用）。透传到 <see cref="Chat"/>。</summary>
    public Task RefreshLlmConfigsAsync() => Chat.RefreshLlmConfigsAsync();

    // —— 侧边栏（原样：展开/收起）——
    [ObservableProperty]
    private double _sidebarWidth = 220;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 220 : 48;
        _sidebarCollapsedForNotes = false;
    }

    private bool _sidebarCollapsedForNotes;

    // —— 导航 ——
    [ObservableProperty]
    private string _currentPage = "notes";

    /// <summary>是否是笔记页</summary>
    public bool IsNotesPage => CurrentPage == "notes";

    /// <summary>是否是专家页</summary>
    public bool IsExpertsPage => CurrentPage == "experts";

    /// <summary>是否是自动化页</summary>
    public bool IsAutomationPage => CurrentPage == "automation";

    /// <summary>是否是期货页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsFuturesPage => CurrentPage == "ctp";

    /// <summary>是否是回测/实盘页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsTradingPage => CurrentPage == "backtest";

    /// <summary>是否是股票交易页（Stockapp 租户专属；Qifu 无此导航，恒 false）。
    /// FlaUI 操控同花顺下单客户端（区别于 backtest 的 Lean 引擎）。</summary>
    public bool IsStockTradePage => CurrentPage == "stocktrade";

    /// <summary>笔记页 ViewModel</summary>
    public NotesViewModel Notes { get; }

    /// <summary>自动化页 ViewModel</summary>
    public AutomationViewModel Automation { get; }

    /// <summary>工作流页 ViewModel</summary>
    public WorkflowsViewModel Workflows { get; }

    /// <summary>规则页 ViewModel</summary>
    public RulesViewModel Rules { get; }

    /// <summary>期货页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 CTP。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Futures { get; protected set; }

    /// <summary>回测/实盘页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 Trading。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Trading { get; protected set; }

    /// <summary>股票交易页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 StockTrading。视图经 App.axaml DataTemplate 解析。</summary>
    public object? StockTrade { get; protected set; }

    /// <summary>专家页 ViewModel</summary>
    public ExpertsViewModel Experts { get; }

    /// <summary>导航项集合（租户填充）。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    partial void OnCurrentPageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsNotesPage));
        OnPropertyChanged(nameof(IsExpertsPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsFuturesPage));
        OnPropertyChanged(nameof(IsTradingPage));
        OnPropertyChanged(nameof(IsStockTradePage));
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        // 离开笔记页时恢复侧边栏
        if (CurrentPage == "notes" && pageKey != "notes" && _sidebarCollapsedForNotes)
        {
            IsSidebarExpanded = true;
            SidebarWidth = 220;
            _sidebarCollapsedForNotes = false;
        }

        // 进入笔记页：自动收起侧边栏给笔记更大空间
        if (pageKey == "notes" && IsSidebarExpanded)
        {
            IsSidebarExpanded = false;
            SidebarWidth = 48;
            _sidebarCollapsedForNotes = true;
        }

        CurrentPage = pageKey;
        foreach (var item in NavItems)
            item.IsCurrent = item.Key == pageKey;
    }

    protected void AddNav(string key, string icon, string title)
    {
        NavItems.Add(new NavItem
        {
            Key = key,
            Icon = icon,
            Title = title,
            Command = NavigateToCommand,
        });
    }

    // —— 账户 / 登录态 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private string _userName = "未登录";

    /// <summary>账户头像首字母（未登录显示 ?）。</summary>
    public string UserInitial => IsLoggedIn
        ? (string.IsNullOrEmpty(UserName) ? "?" : UserName[..1])
        : "?";

    /// <summary>启动期初始化：登录态 + 业务 VM 加载 + 聊天引擎初始化 + 调度器/MCP 启动。
    /// 业务 VM 须先于 <see cref="Chat"/> 初始化（Chat.RestoreSummonedAndSelectedAsync 读这三类列表）。</summary>
    public virtual async Task InitializeAsync()
    {
        try
        {
            if (await _auth.IsLoggedInAsync())
            {
                IsLoggedIn = true;
                var username = await _tokenStore.GetUsernameAsync();
                UserName = string.IsNullOrWhiteSpace(username) ? "已登录" : username;
            }
            else
            {
                IsLoggedIn = false;
                UserName = "未登录";
            }
        }
        catch
        {
            IsLoggedIn = false;
            UserName = "未登录";
        }

        // 业务 VM 加载（Chat 恢复召唤/选中态依赖 Experts/Workflows/Rules 列表已就绪）
        await Notes.LoadAsync();
        await Experts.LoadAsync();
        await Automation.LoadAsync();
        await Workflows.LoadAsync();
        await Rules.LoadAsync();

        // 聊天引擎初始化：LLM 配置 + 召唤/选中恢复 + 最近会话 + 连接态 + 自动连接（自管 _isInitializing）
        await Chat.InitializeAsync();

        // 启动自动化调度器（进程内定时触发；仅 App 打开时生效）。双启动守卫已在调度器内。
        _ = _serviceProvider.GetRequiredService<IAutomationService>()
            .StartSchedulerAsync(CancellationToken.None);

        // 启动 MCP server 连接（若 mcp-servers 目录配了 server）。后台连接 + 连好后把工具注册进 ToolRegistry。
        // 用 shared-task：编辑器打开时 EnsureLoadedAsync 会 await 同一连接任务，确保 MCP 区有最新工具。
        _ = Task.Run(async () =>
        {
            try
            {
                if (_serviceProvider.GetService<IMcpManagementService>() is { } mgmt)
                {
                    await mgmt.EnsureStartedAsync();
                    mgmt.StartHealthMonitoringAsync(TimeSpan.FromMinutes(2)).SafeFireAndForget(); // 每 2 分钟探活，掉线自动重连
                }
            }
            catch { /* MCP 启动失败不阻断主流程 */ }
        });
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (await _loginDialog.ShowLoginAsync())
            await InitializeAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _auth.LogoutAsync();
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        await _settingsDialog.ShowSettingsAsync();
    }
}
