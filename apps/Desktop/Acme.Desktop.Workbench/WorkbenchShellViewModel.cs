using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Workspace;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Workbench;

/// <summary>
/// Codex 工作台 shell ViewModel：复用 <see cref="MainViewModelBase"/>（账号/登录/业务 VM/Chat 引擎/调度器/MCP），
/// 叠加工作台层——<see cref="ActiveWorkspace"/>（Workspace 决定布局，非 Window）+ 主题钩子 + 工作台导航 rail。
/// </summary>
public partial class WorkbenchShellViewModel : MainViewModelBase
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly IRegionManager _regionManager;
    private readonly WorkspaceDefinition _defaultWorkspace;
    private INotifyPropertyChanged? _navWorkspace;

    public WorkbenchShellViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog,
        IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider,
        IDeviceChatClient deviceChatClient, ILlmConfigService llmService,
        IWorkspaceManager workspaceManager, IRegionManager regionManager, WorkspaceDefinition defaultWorkspace,
        string tenant = "codex")
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService, tenant)
    {
        _workspaceManager = workspaceManager;
        _regionManager = regionManager;
        _defaultWorkspace = defaultWorkspace;
        _workspaceManager.WorkspaceActivated += OnWorkspaceActivated;
    }

    /// <summary>工作台导航 rail 项（数据驱动：从活动工作台的首个 LayoutTabbed 派生，覆盖租户 panel 差异）。
    /// 独立于基类 NavItems——本 shell 用工作台布局，不用 NavItems 切中间内容。</summary>
    public ObservableCollection<NavItem> WorkbenchNavItems { get; } = new();

    /// <summary>活动工作台（Window 主区 ContentControl 绑此 → DataTemplate → WorkspaceView）。</summary>
    [ObservableProperty]
    private IWorkspace? _activeWorkspace;

    /// <summary>主题名（"light" 内置；dark 留后续）。</summary>
    [ObservableProperty]
    private string _theme = "light";

    /// <summary>自定义主题 JSON（null = 仅内置 light；非空 = ThemeLoader 叠加 overlay）。</summary>
    [ObservableProperty]
    private string? _customThemeJson;

    private void OnWorkspaceActivated(object? sender, IWorkspace ws)
    {
        // 解订阅旧工作台的 SelectedTabIndex 变更，订阅新工作台——直接点 TabControl tab 时同步 nav 高亮。
        if (_navWorkspace is not null)
            _navWorkspace.PropertyChanged -= OnWorkspaceSelectedTabChanged;
        _navWorkspace = ws as INotifyPropertyChanged;
        if (_navWorkspace is not null)
            _navWorkspace.PropertyChanged += OnWorkspaceSelectedTabChanged;

        ActiveWorkspace = ws;
        RebuildNav(ws);
        UpdateNavCurrent();
    }

    private void OnWorkspaceSelectedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IWorkspace.SelectedTabIndex))
            UpdateNavCurrent();
    }

    /// <summary>从活动工作台的首个 LayoutTabbed 节点派生导航 rail 项（数据驱动：租户 panel 差异由工作台定义决定）。</summary>
    private void RebuildNav(IWorkspace ws)
    {
        WorkbenchNavItems.Clear();
        var tabbed = LayoutNode.FindFirstTabbed(ws.Layout);
        if (tabbed is null) return;
        foreach (var rid in tabbed.RegionIds)
        {
            var panel = ws.Panels.FirstOrDefault(p => p.RegionId == rid);
            if (panel is null) continue;
            // panel.Icon 是 key（icons.json manifest）或直接 path data；IconLoader 解析 key，未命中回退原串。
            var icon = IconLoader.GetPathData(panel.Icon) ?? panel.Icon;
            WorkbenchNavItems.Add(new NavItem
            {
                Key = panel.Key,
                Icon = icon,
                Title = panel.Title,
                Command = SwitchContentCommand,
            });
        }
    }

    /// <summary>导航 rail 切内容：映射 panel Key → 工作台 tab 索引（按首个 LayoutTabbed 的 RegionId 顺序）。</summary>
    [RelayCommand]
    private void SwitchContent(string key)
    {
        if (ActiveWorkspace is null) return;
        var tabbed = LayoutNode.FindFirstTabbed(ActiveWorkspace.Layout);
        if (tabbed is null) return;
        for (int i = 0; i < tabbed.RegionIds.Count; i++)
        {
            var panel = ActiveWorkspace.Panels.FirstOrDefault(p => p.RegionId == tabbed.RegionIds[i]);
            if (panel is not null && panel.Key == key)
            {
                ActiveWorkspace.SelectedTabIndex = i;
                break;
            }
        }
        UpdateNavCurrent();
    }

    private void UpdateNavCurrent()
    {
        var idx = ActiveWorkspace?.SelectedTabIndex ?? 0;
        var tabbed = ActiveWorkspace is null ? null : LayoutNode.FindFirstTabbed(ActiveWorkspace.Layout);
        string? currentKey = null;
        if (tabbed is not null && idx >= 0 && idx < tabbed.RegionIds.Count)
        {
            var rid = tabbed.RegionIds[idx];
            currentKey = ActiveWorkspace!.Panels.FirstOrDefault(p => p.RegionId == rid)?.Key;
        }
        foreach (var item in WorkbenchNavItems)
            item.IsCurrent = item.Key == currentKey;
    }

    /// <summary>测试插件面板注入（Phase 3 验证）：往命名 region "inject" 塞一个 test panel，验证 RegionManager 路径。</summary>
    [RelayCommand]
    private void InjectTestPanel()
        => _regionManager.AddToRegion("inject", new PanelViewModel("test", "test", "插件测试", "test", null));

    /// <summary>启动期：base 初始化（账号/业务 VM/Chat/调度器/MCP）后激活默认工作台。</summary>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await _workspaceManager.ActivateAsync(_defaultWorkspace);
    }
}
