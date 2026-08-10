using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IPreferencesStore _preferences;
    private INotifyPropertyChanged? _navWorkspace;

    /// <summary>布局持久化键（与 WorkspaceView 共用；每租户独立 preferences.json）。</summary>
    private const string LayoutPrefKey = "workbench.layout";

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
        _preferences = serviceProvider.GetRequiredService<IPreferencesStore>();
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

    /// <summary>启动期：base 初始化（账号/业务 VM/Chat/调度器/MCP）后激活工作台。
    /// 激活前合并已持久化的布局（用户拖动后的 GridSplitter 位置 + tab 选择）——IWorkspace.Layout 只读，
    /// 必须在建工作台前合并进 definition，不能改已建工作台。</summary>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var def = await RestoreLayoutAsync(_defaultWorkspace);
        await _workspaceManager.ActivateAsync(def);
    }

    /// <summary>从偏好存储恢复用户自定义布局。校验区域集与 definition 一致（面板增删则回退默认），反序列化失败也回退（不崩）。</summary>
    private async Task<WorkspaceDefinition> RestoreLayoutAsync(WorkspaceDefinition def)
    {
        try
        {
            var json = await _preferences.GetAsync(LayoutPrefKey);
            if (string.IsNullOrWhiteSpace(json)) return def;
            var layout = JsonSerializer.Deserialize<LayoutNode>(json);
            if (layout is null || !CoversSameRegions(layout, def.Layout)) return def; // 结构变 → 回退
            return def with { Layout = layout };
        }
        catch
        {
            return def; // 反序列化失败 → 回退（不崩）
        }
    }

    /// <summary>两棵布局树覆盖相同的 region id 集合（面板增删会不等 → 回退默认，免用过期结构）。</summary>
    private static bool CoversSameRegions(LayoutNode a, LayoutNode b)
        => CollectRegions(a).SetEquals(CollectRegions(b));

    /// <summary>收集布局树中所有 region id（LayoutRegion.RegionId + LayoutTabbed.RegionIds）。</summary>
    private static HashSet<string> CollectRegions(LayoutNode n)
    {
        var set = new HashSet<string>();
        void Walk(LayoutNode x)
        {
            switch (x)
            {
                case LayoutSplit s:
                    foreach (var c in s.Children) Walk(c);
                    break;
                case LayoutRegion r:
                    set.Add(r.RegionId);
                    break;
                case LayoutTabbed t:
                    foreach (var id in t.RegionIds) set.Add(id);
                    break;
            }
        }
        Walk(n);
        return set;
    }

    /// <summary>清除已保存的布局并重新激活默认工作台（重置 GridSplitter 位置 + tab 选择）。
    /// 重新激活会建新 WorkspaceView；旧 view 的 OnHostClosing 守卫（_workspace != Active）确保关闭时不覆盖重置。</summary>
    [RelayCommand]
    private async Task ResetLayout()
    {
        await _preferences.SetAsync(LayoutPrefKey, null);
        await _workspaceManager.ActivateAsync(_defaultWorkspace);
    }
}
