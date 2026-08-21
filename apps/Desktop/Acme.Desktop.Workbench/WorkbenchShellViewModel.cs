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
    private ITerminalService? _terminalService;
    private IChatPanelService? _chatPanelService;
    private INotifyPropertyChanged? _navWorkspace;

    /// <summary>布局持久化键（与 WorkspaceView 共用；每租户独立 preferences.json）。</summary>
    private const string LayoutPrefKey = "workbench.layout";

    /// <summary>主题选择持久化键（存主题名：light/dark/用户文件名）。</summary>
    private const string ThemePrefKey = "ui.theme";

    /// <summary>侧边栏展开/收起持久化键（"true"/"false"，默认收起=只图标）。</summary>
    private const string SidebarPrefKey = "ui.sidebarExpanded";

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
        // 终端面板显隐（方案 B toggle）：初始态从 service 读 + 订阅变更同步 IsTerminalVisible（侧边栏 toggle 按钮绑此）。
        _terminalService = serviceProvider.GetService<ITerminalService>();
        if (_terminalService is not null)
        {
            _isTerminalVisible = _terminalService.IsPanelVisible;
            _terminalService.PanelVisibilityChanged += OnTerminalPanelVisibilityChanged;
        }
        // AI 聊天面板显隐（VSCode 式可折叠右栏，对齐 ChatGPT 桌面端）：初始态从 service 读 + 订阅变更同步 IsChatVisible（侧边栏 toggle 按钮绑此）。
        _chatPanelService = serviceProvider.GetService<IChatPanelService>();
        if (_chatPanelService is not null)
        {
            _isChatVisible = _chatPanelService.IsChatVisible;
            _chatPanelService.ChatVisibilityChanged += OnChatVisibilityChanged;
        }
    }

    /// <summary>工作台导航 rail 项（数据驱动：从活动工作台的首个 LayoutTabbed 派生，覆盖租户 panel 差异）。
    /// 本 shell 用工作台布局切中间内容——基类已无 nav 系统，nav 完全由此属性驱动。</summary>
    public ObservableCollection<NavItem> WorkbenchNavItems { get; } = new();

    /// <summary>活动工作台（Window 主区 ContentControl 绑此 → DataTemplate → WorkspaceView）。</summary>
    [ObservableProperty]
    private IWorkspace? _activeWorkspace;

    /// <summary>工作台是否已激活（ActiveWorkspace != null）。窗口中间区按此切「加载占位」vs「真实工作台」——
    /// 首帧窗口 Show 到 ActivateAsync 之间有个短暂空窗，用占位避免裸露空白。</summary>
    public bool HasActiveWorkspace => ActiveWorkspace is not null;

    partial void OnActiveWorkspaceChanged(IWorkspace? value) => OnPropertyChanged(nameof(HasActiveWorkspace));

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

    /// <summary>是否显示动画梦幻背景层（aurora 漂移光斑）。由主题 JSON 的 animated 字段驱动（dream 类皮肤 true）。
    /// WorkbenchWindow 订阅此属性变化启停 aurora；普通主题 false = 静态背景，aurora 停跑省 CPU。</summary>
    [ObservableProperty]
    private bool _isDreamLayerVisible;

    /// <summary>是否无边框自绘标题栏（dream 类炫皮肤 borderless=true）。由主题 JSON 的 chrome.borderless 字段驱动。
    /// WorkbenchWindow 订阅此属性变化切 ExtendClientAreaToDecorationsHint + 显隐自绘标题栏；普通主题 false = 系统原生 chrome。</summary>
    [ObservableProperty]
    private bool _isBorderless;

    /// <summary>自绘标题栏高度（px）。borderless=true 时生效，默认 36。由主题 JSON 的 chrome.titleBarHeight 字段驱动。</summary>
    [ObservableProperty]
    private double _titleBarHeight = 36;

    /// <summary>底部终端面板是否可见（侧边栏终端 toggle 按钮绑此 + 驱动高亮态）。
    /// 状态由 <see cref="ITerminalService.PanelVisibilityChanged"/> 事件同步——TerminalView 折叠按钮 / 本 VM 的 ToggleTerminalCommand 都经 service 改态，事件回写此属性。</summary>
    [ObservableProperty]
    private bool _isTerminalVisible = true;

    /// <summary>右侧 AI 聊天面板是否可见（侧边栏「AI 聊天」toggle 按钮绑此 + 驱动高亮态）。
    /// 状态由 <see cref="IChatPanelService.ChatVisibilityChanged"/> 事件同步——聊天面板内 ✕ 按钮 / 本 VM 的 ToggleChatCommand 都经 service 改态，事件回写此属性。</summary>
    [ObservableProperty]
    private bool _isChatVisible = true;

    /// <summary>侧边栏是否展开（true=图标+导航文字 200px / false=仅图标 48px）。当前默认收起（仅图标）。
    /// 纯 shell 窗口态（侧栏 Border 宽 + nav 项文字显隐经 Classes.expanded 绑定 + SharedTheme 样式），
    /// 不经 service（侧栏在工作区 ContentControl 之外，WorkspaceView 无需响应）。持久化 <see cref="SidebarPrefKey"/>。</summary>
    [ObservableProperty]
    private bool _isSidebarExpanded;

    /// <summary>主题变化（overlay JSON 或明暗倾向改变）时触发。子窗口（设置/Trace）订阅以即时重染——
    /// 它们不在 Workbench 层、拿不到 ThemeLoader，经 <see cref="IThemeApplier"/> 桥接。</summary>
    public event EventHandler? ThemeChanged;

    /// <inheritdoc/>
    bool IThemeHost.IsDark => IsDarkVariant;

    // overlay JSON 或明暗倾向一变即通知子窗口重染（CustomThemeJson 由 [ObservableProperty] 生成 public 属性，已满足接口）。
    partial void OnCustomThemeJsonChanged(string? value) => ThemeChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsDarkVariantChanged(bool value) => ThemeChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsBorderlessChanged(bool value) => ThemeChanged?.Invoke(this, EventArgs.Empty);
    partial void OnTitleBarHeightChanged(double value) => ThemeChanged?.Invoke(this, EventArgs.Empty);

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
        // ★ 先激活工作台（纯布局 + nav，不依赖登录态/业务数据/Chat）——让窗口 Show 后中间内容区立即填充，
        // 而非「侧栏先有、工作区空白等一堆异步（登录态+业务 LoadAsync+Chat）完成后才填」。登录态/数据加载随后补。
        await LoadThemesAsync();
        var savedTheme = await _preferences.GetAsync(ThemePrefKey);
        await SelectThemeCoreAsync(savedTheme ?? "light", default);
        // 侧边栏展开/收起态恢复（默认收起=仅图标；持久化 "true" 才展开）。
        var savedSidebar = await _preferences.GetAsync(SidebarPrefKey);
        IsSidebarExpanded = savedSidebar == "true";
        var def = await RestoreLayoutAsync(_defaultWorkspace);
        await _workspaceManager.ActivateAsync(def);

        // 登录态 + 业务 VM 加载 + 聊天引擎初始化 + 调度器/MCP 启动（延后到工作台已上屏之后，
        // 用户先看到工作台壳，再填数据——比先等数据再显示工作台感知更快）。
        await base.InitializeAsync();
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
    /// 内置主题（light/dark）= 内嵌 Assets/{name}.json 的 JSON overlay；
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
        ApplyThemeState(name, json);   // 设内存态（IsDarkVariant/动画层/chrome/CustomThemeJson/Theme/IsCurrent）
        await _preferences.SetAsync(ThemePrefKey, name, ct);
    }

    /// <summary>★ 启动同步主题恢复：读持久化 ui.theme + 对应 JSON，设全部内存态——在窗口 Show 之前同步调，
    /// 使第一帧即渲染选中主题（免「先 flash 默认 light 再切真主题」）。不写 pref（恢复 ≠ 选中）、不 await。
    /// 与 <see cref="InitializeAsync"/> 里 FireAndForget 的异步主题恢复互补：二者都收敛于 <see cref="ApplyThemeState"/>，
    /// 本方法只做「读 + 设内存态」的同步子集，其余（账号/业务 VM/MCP 连接）仍走异步不阻塞启动。</summary>
    public void RestoreThemeSync()
    {
        if (Themes.Count == 0) LoadThemesAsync(); // 确保主题列表已填（IsCurrent 高亮依赖它）
        var name = _preferences.GetAsync(ThemePrefKey).GetAwaiter().GetResult(); // GetAsync 同步完成（Task.FromResult），UI 线程安全
        if (string.IsNullOrWhiteSpace(name)) name = "light";
        string? json;
        if (ThemeLoader.IsBuiltIn(name))
        {
            json = ReadUserThemeJsonSync(name) ?? ThemeLoader.GetBuiltInJson(name);
        }
        else
        {
            json = ReadUserThemeJsonSync(name);
        }
        if (json is null) { name = "light"; json = ThemeLoader.GetLightJson(); }
        ApplyThemeState(name, json);
    }

    /// <summary>把「主题名 + JSON」落地为内存态：deserialize → IsDarkVariant/IsDreamLayerVisible/chrome/
    /// CustomThemeJson/Theme/IsCurrent。选中与恢复两路径共用，保证状态一致。</summary>
    private void ApplyThemeState(string name, string? json)
    {
        // 深色倾向：读 JSON variant 字段（"light"→false，缺省/其它→true）。
        // 所有内置主题 JSON 均带 variant 字段；驱动 Avalonia 原生控件 ThemeVariant——overlay 不覆盖原生控件，须靠 variant 翻转。
        var def = ThemeLoader.Deserialize(json ?? "");
        IsDarkVariant = !string.Equals(def?.Variant, "light", StringComparison.OrdinalIgnoreCase);
        IsDreamLayerVisible = def?.Animated == true;   // dream 类皮肤开 aurora 动画背景层
        ApplyChrome(def);   // dream 类皮肤 borderless=true → 无边框自绘标题栏
        CustomThemeJson = json;   // 触发 ThemeLoader.Apply（窗口订阅 CustomThemeJson 变化）
        Theme = name;             // 当前名（高亮）
        foreach (var t in Themes)
            t.IsCurrent = t.Name == name;
    }

    /// <summary>读用户主题 JSON 文件。文件不存在返 null（调用方回退 light）。</summary>
    private async Task<string?> ReadUserThemeJsonAsync(string name, CancellationToken ct)
    {
        var dir = DesktopOptions.ThemesDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, name + ".json");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    /// <summary>同步读用户主题 JSON（启动期 <see cref="RestoreThemeSync"/> 用；文件不存在返 null）。</summary>
    private string? ReadUserThemeJsonSync(string name)
    {
        var dir = DesktopOptions.ThemesDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, name + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>UI 绑定的选择命令（账号菜单 flyout + 设置「外观」tab）。</summary>
    [RelayCommand]
    private async Task SelectTheme(string name)
        => await SelectThemeCoreAsync(name ?? "light", default);

    /// <inheritdoc/>
    Task IThemeHost.SelectThemeAsync(string name, CancellationToken cancellationToken)
        => SelectThemeCoreAsync(name, cancellationToken);

    /// <summary>从主题定义读 chrome 字段设 <see cref="IsBorderless"/>/<see cref="TitleBarHeight"/>。
    /// null def / 无 chrome = 系统原生 chrome（borderless=false, 高度保持默认）。两处主题设置路径共用。</summary>
    private void ApplyChrome(ThemeDefinition? def)
    {
        IsBorderless = def?.Chrome?.Borderless == true;
        if (def?.Chrome?.TitleBarHeight is { } h and > 0)
            TitleBarHeight = h;
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

    /// <summary>两棵布局树覆盖相同的 region id 序列（有序比较：面板增删或 tab 顺序变更都会不等 → 回退默认）。
    /// 有序而非集合：当代码默认 tab 顺序变化（如某面板提首位），旧持久化布局顺序不匹配 → 回退新默认，免被旧顺序覆盖。</summary>
    private static bool CoversSameRegions(LayoutNode a, LayoutNode b)
        => CollectRegions(a).SequenceEqual(CollectRegions(b));

    /// <summary>按遍历顺序收集布局树中所有 region id（LayoutSplit 按 Children 顺序、LayoutTabbed 按 RegionIds 数组顺序）。</summary>
    private static List<string> CollectRegions(LayoutNode n)
    {
        var list = new List<string>();
        void Walk(LayoutNode x)
        {
            switch (x)
            {
                case LayoutSplit s:
                    foreach (var c in s.Children) Walk(c);
                    break;
                case LayoutRegion r:
                    list.Add(r.RegionId);
                    break;
                case LayoutTabbed t:
                    foreach (var id in t.RegionIds) list.Add(id);
                    break;
            }
        }
        Walk(n);
        return list;
    }

    /// <summary>清除已保存的布局并重新激活默认工作台（重置 GridSplitter 位置 + tab 选择）。
    /// 重新激活会建新 WorkspaceView；旧 view 的 OnHostClosing 守卫（_workspace != Active）确保关闭时不覆盖重置。</summary>
    [RelayCommand]
    private async Task ResetLayout()
    {
        await _preferences.SetAsync(LayoutPrefKey, null);
        await _workspaceManager.ActivateAsync(_defaultWorkspace);
    }

    /// <summary>侧边栏终端 toggle：切换底部终端面板显隐（PTY 不中断，仅折叠/展开 region 空间）。
    /// 经 service 改态 → service 触发 PanelVisibilityChanged → WorkspaceView 折叠/展开 region + 回写 IsTerminalVisible。</summary>
    [RelayCommand]
    private void ToggleTerminal()
        => _terminalService?.SetPanelVisible(!IsTerminalVisible);

    private void OnTerminalPanelVisibilityChanged(bool visible) => IsTerminalVisible = visible;

    /// <summary>侧边栏「AI 聊天」toggle：切换右侧聊天面板显隐（聊天视图不销毁，仅折叠/展开 region 空间，流式不中断）。
    /// 经 service 改态 → service 触发 ChatVisibilityChanged → WorkspaceView 折叠/展开 region + 回写 IsChatVisible。</summary>
    [RelayCommand]
    private void ToggleChat()
        => _chatPanelService?.SetChatVisible(!IsChatVisible);

    private void OnChatVisibilityChanged(bool visible) => IsChatVisible = visible;

    /// <summary>侧边栏展开/收起 toggle：切 IsSidebarExpanded + 持久化（fire-and-forget，SafeFireAndForget 记 Trace）。
    /// 纯 shell 态，无 service 回路——窗口 XAML 经 Classes.expanded 绑定即时切宽 + nav 文字显隐。</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        Task.Run(async () => await _preferences.SetAsync(SidebarPrefKey, IsSidebarExpanded ? "true" : "false"))
            .SafeFireAndForget();
    }
}
