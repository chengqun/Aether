using Aether.AI.Core.Agents;
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
/// 桌面 shell 主 ViewModel 基类：可收起侧边栏 + 账户/登录态 + 同级业务 VM（<see cref="IShellHost"/>）。
/// 导航由 <c>WorkbenchShellViewModel</c> 的工作台布局数据驱动（<c>WorkbenchNavItems</c>），不在本类——
/// 本类只持 shell/账号/业务 VM + 聊天引擎（<see cref="Chat"/>）。ChatView 绑定 <c>{Binding Chat}</c>，不经本类透传聊天属性。
/// 租户专属页 VM（期货/回测/股票）不在本类——由租户 shell VM 自持，经 IViewRegistryContributor 注入工作台。
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

    /// <summary>初始化期守卫：InitializeAsync 并行加载业务 VM 时，抑制 catalog Changed 的回流重复加载
    ///（初始化里已显式加载全部列表，回流是多余的，且并发改 ObservableCollection 不安全）。</summary>
    private volatile bool _initializing;

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

        Documents = new DocumentsViewModel(serviceProvider,
            serviceProvider.GetRequiredService<ICurrentProjectDirectoryProvider>());
        SourceControl = new SourceControlViewModel(serviceProvider,
            serviceProvider.GetRequiredService<ICurrentProjectDirectoryProvider>());
        Terminal = new TerminalViewModel(serviceProvider,
            serviceProvider.GetRequiredService<ICurrentProjectDirectoryProvider>());
        Browser = new BrowserViewModel(serviceProvider);
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
        Rules = new RulesViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<McpViewModel>());

        // labs 目录变更（目录版 catalog 的 Changed 信号，由 LabsDirectoryWatcher 驱动）按「涉及的目录」定向刷新——
        // 不再无条件重载聊天目标表。改 knowledge/（知识对象）/strategies/（策略）等与聊天目标无关的文件，
        // 不该触发 Experts/Rules/Workflows 重载、更不该刷新聊天下拉（那会重定向 SelectedChatTarget，
        // 历史上曾导致「改个知识对象就把正在进行的聊天会话清空」）。
        var catalogSource = serviceProvider.GetService<Acme.Notes.Domain.Catalog.INoteCatalogSource>();
        if (catalogSource is not null)
        {
            catalogSource.Changed += async (changedRelativePath) =>
            {
                if (_initializing) return;

                // 解析变更涉及的首段目录名集合；null（未知/缓冲溢出）→ 视为「全部涉及」，退化到全量重载（但不误清聊天，见 ChatWorkspaceViewModel）。
                var changedDirs = ParseChangedDirs(changedRelativePath);
                bool AllOr(string dir) => changedDirs is null || changedDirs.Contains(dir);

                if (AllOr("agents")) await Experts.LoadAsync();
                if (AllOr("workflows")) await Workflows.LoadAsync();
                if (AllOr("rules") || AllOr("coderules")) await Rules.LoadAsync();
                // 技能在 labs/skills/——仅在技能目录变更时刷新 SkillRegistry 索引 + 技能 tab 卡片。
                if (AllOr("skills"))
                    serviceProvider.GetService<SkillViewModel>()?.LoadAsync().SafeFireAndForget();

                // 仅当涉及聊天目标目录（agents/rules/coderules/workflows）时才通知聊天下拉刷新新增项；
                // 知识对象/技能/策略/MCP 等无关变更绝不触及聊天选中态。
                if (AllOr("agents") || AllOr("workflows") || AllOr("rules") || AllOr("coderules"))
                {
                    Experts.NotifyTargetsChanged();
                    Workflows.NotifyTargetsChanged();
                    Rules.NotifyTargetsChanged();
                }
            };
        }

        Experts = new ExpertsViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<McpViewModel>(),
            serviceProvider.GetService<IConnectorViewModel>());

        Activity = new ActivityViewModel(serviceProvider,
            serviceProvider.GetRequiredService<ICurrentProjectDirectoryProvider>(),
            serviceProvider.GetRequiredService<IKnowledgeDirectoryProvider>());

        // labs 页（文档版知识对象独立 nav 页，现为 9 类型统一编辑器）：根目录=独立知识库目录（IKnowledgeDirectoryProvider，
        // 与文档页项目目录解耦——避免知识库目录覆盖 ICurrentProjectDirectoryProvider 致 SCM/终端/活动页错乱）。
        // this 作 IShellHost 供 form 类型编辑器复用 Experts/Rules/Workflows 同级 VM（惰性访问，此时已构造完成）。
        Knowledge = new KnowledgeObjectsViewModel(serviceProvider,
            serviceProvider.GetRequiredService<IKnowledgeDirectoryProvider>(),
            this,
            Tenant);

        // 聊天引擎组合：须在 Experts/Workflows/Rules 建好后构造——它订阅三者的 TargetsChanged 事件
        //（可选目标集合变更 → 刷新统一下拉池），并在 InitializeAsync 里读这三类列表恢复选中态。
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

    /// <summary>打开文档页 ViewModel（多标签磁盘文件编辑器）</summary>
    public DocumentsViewModel Documents { get; }

    /// <summary>源代码管理页 ViewModel（git status/diff/stage/commit/discard，读文档页打开的项目目录）</summary>
    public SourceControlViewModel SourceControl { get; }

    /// <summary>集成终端页 ViewModel（多标签真实 PTY；agent run_in_terminal 工具亦经其 ITerminalService）</summary>
    public TerminalViewModel Terminal { get; }

    /// <summary>浏览器网络页 ViewModel（Playwright 持久会话 + 实时记录 HTTP 请求；agent browser_open/get_cookies 工具亦经其 IBrowserService）</summary>
    public BrowserViewModel Browser { get; }

    /// <summary>自动化页 ViewModel</summary>
    public AutomationViewModel Automation { get; }

    /// <summary>工作流页 ViewModel</summary>
    public WorkflowsViewModel Workflows { get; }

    /// <summary>规则页 ViewModel</summary>
    public RulesViewModel Rules { get; }

    /// <summary>专家页 ViewModel</summary>
    public ExpertsViewModel Experts { get; }

    /// <summary>活动热力图页 ViewModel（GitHub 式贡献图：Agent 执行 + Git 提交按日聚合，读文档页项目目录的 git 历史）。</summary>
    public ActivityViewModel Activity { get; }

    /// <summary>文档版知识对象页 ViewModel（独立 nav 页：左 .md 树 + 右上下两栏表单/源码，根目录=独立知识库目录）。</summary>
    public KnowledgeObjectsViewModel Knowledge { get; }

    // —— 账户 / 登录态 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    [NotifyPropertyChangedFor(nameof(AccountTooltip))]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    [NotifyPropertyChangedFor(nameof(AccountTooltip))]
    private string _userName = "未登录";

    /// <summary>账户头像首字母（未登录显示 ?）。</summary>
    public string UserInitial => IsLoggedIn
        ? (string.IsNullOrEmpty(UserName) ? "?" : UserName[..1])
        : "?";

    /// <summary>侧栏底部账户按钮 Tooltip——未登录时带「点击登录」引导（人形占位图标配「未登录」裸文案不够指向）。</summary>
    public string AccountTooltip => IsLoggedIn ? UserName : "未登录 · 点击登录";

    /// <summary>启动期初始化：登录态 + 业务 VM 加载 + 聊天引擎初始化 + 调度器/MCP 启动。
    /// 业务 VM 须先于 <see cref="Chat"/> 初始化（Chat 恢复选中目标读这三类列表）。</summary>
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

        // 业务 VM 加载（Chat 恢复选中目标依赖 Experts/Workflows/Rules 列表已就绪）。
        // 四个相互独立——并行加载缩短「窗口出现到可用」间隔（每个内部 I/O 并行、集合操作仍经 UI 线程序列化）。
        // _initializing 守卫抑制此期间的 catalog Changed 回流（避免与并行加载重复 + 并发改集合）。
        _initializing = true;
        try
        {
            await Task.WhenAll(
                Experts.LoadAsync(),
                Automation.LoadAsync(),
                Workflows.LoadAsync(),
                Rules.LoadAsync());
        }
        finally { _initializing = false; }

        // 聊天引擎初始化：LLM 配置 + 选中目标恢复 + 最近会话 + 连接态 + 自动连接（自管 _isInitializing）
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

    /// <summary>解析 <c>LabsDirectoryWatcher</c> 广播的变更路径为「涉及的首段目录名集合」。
    /// <paramref name="changedRelativePath"/> 为 labs 相对路径（单目录名，或多个以 <c>|</c> 分隔）；
    /// null 表示未知（缓冲溢出/路径不可得）→ 返回 null 触发全量。空串返回空集合。</summary>
    private static HashSet<string>? ParseChangedDirs(string? changedRelativePath)
    {
        if (changedRelativePath is null) return null; // 未知 → 全量
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in changedRelativePath.Split('|', StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(seg);
        return dirs;
    }
}
