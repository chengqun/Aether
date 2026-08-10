using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Theming;
using Acme.Desktop.Workbench.Workspace;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 工作台 shell ViewModel：复用 <see cref="MainViewModelBase"/>（账号/登录/业务 VM/Chat 引擎/调度器/MCP），
/// 叠加工作台层——<see cref="ActiveWorkspace"/>（Workspace 决定布局，非 Window）+ 主题钩子 + 工作台导航 rail。
/// 实现 <see cref="IThemeHost"/>：主题状态（<see cref="Theme"/>/<see cref="CustomThemeJson"/>）在此，
/// 设置层 SettingsVM 经接口跨层访问（不引 Workbench）；账号菜单 flyout 直接绑此 VM。
/// </summary>
public partial class WorkbenchShellViewModel : MainViewModelBase, IThemeHost
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly WorkspaceDefinition _defaultWorkspace;
    private readonly IPreferencesStore _preferences;
    private readonly IServiceProvider _serviceProvider;
    private INotifyPropertyChanged? _navWorkspace;

    /// <summary>布局持久化键（与 WorkspaceView 共用；每租户独立 preferences.json）。</summary>
    private const string LayoutPrefKey = "workbench.layout";

    /// <summary>主题选择持久化键（存主题名：light/dark/用户文件名）。</summary>
    private const string ThemePrefKey = "ui.theme";

    public WorkbenchShellViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog,
        IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider,
        IDeviceChatClient deviceChatClient, ILlmConfigService llmService,
        IWorkspaceManager workspaceManager, WorkspaceDefinition defaultWorkspace,
        string tenant = "codex")
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService, tenant)
    {
        _workspaceManager = workspaceManager;
        _defaultWorkspace = defaultWorkspace;
        _preferences = serviceProvider.GetRequiredService<IPreferencesStore>();
        _serviceProvider = serviceProvider;
        _workspaceManager.WorkspaceActivated += OnWorkspaceActivated;
    }

    /// <summary>工作台导航 rail 项（数据驱动：从活动工作台的首个 LayoutTabbed 派生，覆盖租户 panel 差异）。
    /// 本 shell 用工作台布局切中间内容——基类已无 nav 系统，nav 完全由此属性驱动。</summary>
    public ObservableCollection<NavItem> WorkbenchNavItems { get; } = new();

    /// <summary>活动工作台（Window 主区 ContentControl 绑此 → DataTemplate → WorkspaceView）。</summary>
    [ObservableProperty]
    private IWorkspace? _activeWorkspace;

    /// <summary>可选主题列表（内置 light/dark + &lt;tenantDir&gt;/themes/*.json）。两 UI 绑此。</summary>
    public ObservableCollection<ThemeOption> Themes { get; } = new();

    // C# 属性无返回类型协变：ObservableCollection 不能隐式实现 IReadOnlyList 接口属性，须显式实现。
    IReadOnlyList<ThemeOption> IThemeHost.Themes => Themes;

    /// <summary>当前主题名（light/dark/用户文件名）。设此属性 + <see cref="CustomThemeJson"/> 触发窗口换肤。</summary>
    [ObservableProperty]
    private string _theme = "light";

    /// <summary>自定义主题 JSON（null = 仅内置 light；非空 = ThemeLoader 叠加 overlay）。</summary>
    [ObservableProperty]
    private string? _customThemeJson;

    /// <summary>当前主题是否深色倾向——驱动 Avalonia 原生控件的 ThemeVariant（light→Light / dark→Dark）。
    /// JSON overlay 只覆盖自定义 token 画刷；原生控件（TextBox/ComboBox/滚动条…）须经 ThemeVariant 翻转。</summary>
    [ObservableProperty]
    private bool _isDarkVariant;

    /// <summary>主题变化（overlay JSON 或明暗倾向改变）时触发。子窗口（设置/Trace）订阅以即时重染——
    /// 它们不在 Workbench 层、拿不到 ThemeLoader，经 <see cref="IThemeApplier"/> 桥接。</summary>
    public event EventHandler? ThemeChanged;

    /// <inheritdoc/>
    bool IThemeHost.IsDark => IsDarkVariant;

    // overlay JSON 或明暗倾向一变即通知子窗口重染（CustomThemeJson 由 [ObservableProperty] 生成 public 属性，已满足接口）。
    partial void OnCustomThemeJsonChanged(string? value) => ThemeChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsDarkVariantChanged(bool value) => ThemeChanged?.Invoke(this, EventArgs.Empty);

    private DesktopOptions? _desktopOptions;
    /// <summary>桌面路径选项（惰性从 DI 取，同 NotesViewModel 的 NotesEditorOptions 模式）。</summary>
    private DesktopOptions DesktopOptions
        => _desktopOptions ??= _serviceProvider.GetService<IOptions<DesktopOptions>>()?.Value ?? new();

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

    /// <summary>启动期：base 初始化（账号/业务 VM/Chat/调度器/MCP）后激活工作台。
    /// 激活前合并已持久化的布局（用户拖动后的 GridSplitter 位置 + tab 选择）——IWorkspace.Layout 只读，
    /// 必须在建工作台前合并进 definition，不能改已建工作台。
    /// 主题恢复也在激活前——窗口打开时已应用正确主题（设 CustomThemeJson → 窗口订阅 → ThemeLoader.Apply）。</summary>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await LoadThemesAsync();
        var savedTheme = await _preferences.GetAsync(ThemePrefKey);
        await SelectThemeCoreAsync(savedTheme ?? "light", default);
        var def = await RestoreLayoutAsync(_defaultWorkspace);
        await _workspaceManager.ActivateAsync(def);
    }

    // ── 主题（IThemeHost 实现） ──

    /// <summary>扫描主题列表：内置 light/dark + &lt;tenantDir&gt;/themes/*.json（用户主题，不含同名 light/dark——
    /// 那两个名是内置，用户放同名文件是覆盖内置而非新增条目）。启动调一次，不热重载。
    /// 每个选项注入 SelectThemeCommand——账号菜单子菜单经 Click code-behind 调 SelectCommand，
    /// 避开 $parent[MenuItem] 跨 popup 的绑定歧义。</summary>
    private Task LoadThemesAsync()
    {
        Themes.Clear();
        // 内置主题从 ThemeLoader.BuiltInThemes 清单驱动（新增内置主题只需在清单加一行）。
        foreach (var (name, desc) in ThemeLoader.BuiltInThemes)
            Themes.Add(new ThemeOption(name, desc, true) { SelectCommand = SelectThemeCommand });
        var dir = DesktopOptions.ThemesDirectory;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (ThemeLoader.IsBuiltIn(name)) continue; // 同名文件不覆盖内置
                Themes.Add(new ThemeOption(name, Path.GetFileName(f), false) { SelectCommand = SelectThemeCommand });
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>选中主题：设 overlay JSON + 当前名 + IsCurrent 高亮 + IsDarkVariant + 持久化。供 RelayCommand 与 IThemeHost.SelectThemeAsync 共用。
    /// 内置主题（light/dark/latte/frappe/macchiato/nord/dracula/tokyo-night）= 内嵌 Assets/{name}.json 的 JSON overlay；
    /// 用户可在 &lt;tenantDir&gt;/themes/{name}.json 放同名文件覆盖内置（免重编）。用户自定义主题 = 读文件。</summary>
    private async Task SelectThemeCoreAsync(string name, CancellationToken ct)
    {
        // 内置主题：优先用户同名文件（自定义覆盖），缺则内嵌 JSON。用户主题：读文件。
        var json = ThemeLoader.IsBuiltIn(name)
            ? (await ReadUserThemeJsonAsync(name, ct)) ?? ThemeLoader.GetBuiltInJson(name)
            : await ReadUserThemeJsonAsync(name, ct);
        // 用户主题文件缺失/非法 → 回退内置 light（加载 light.json，免残留指向不存在的主题）。
        if (json is null)
        {
            name = "light";
            json = ThemeLoader.GetLightJson();
        }
        // 深色倾向：读 JSON variant 字段（"light"→false，缺省/其它→true）。
        // 所有内置主题 JSON 均带 variant 字段；驱动 Avalonia 原生控件 ThemeVariant——overlay 不覆盖原生控件，须靠 variant 翻转。
        IsDarkVariant = !string.Equals(ThemeLoader.Deserialize(json ?? "")?.Variant, "light", StringComparison.OrdinalIgnoreCase);
        CustomThemeJson = json;   // 触发 ThemeLoader.Apply（窗口订阅 CustomThemeJson 变化）
        Theme = name;             // 当前名（高亮）
        foreach (var t in Themes)
            t.IsCurrent = t.Name == name;
        await _preferences.SetAsync(ThemePrefKey, name, ct);
    }

    /// <summary>读用户主题 JSON 文件。文件不存在返 null（调用方回退 light）。</summary>
    private async Task<string?> ReadUserThemeJsonAsync(string name, CancellationToken ct)
    {
        var dir = DesktopOptions.ThemesDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, name + ".json");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    /// <summary>UI 绑定的选择命令（账号菜单 flyout + 设置「外观」tab）。</summary>
    [RelayCommand]
    private async Task SelectTheme(string name)
        => await SelectThemeCoreAsync(name ?? "light", default);

    /// <inheritdoc/>
    Task IThemeHost.SelectThemeAsync(string name, CancellationToken cancellationToken)
        => SelectThemeCoreAsync(name, cancellationToken);

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
