using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Shared.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 工作台薄 host：可展开侧栏 + 工作台主区 ContentControl。
/// 不决定布局——布局由 <see cref="WorkspaceView"/> 按 ActiveWorkspace.Layout 构建。
/// 主题：订阅 VM 的 Theme/CustomThemeJson 变化 → <see cref="ThemeLoader.Apply"/> 叠加 JSON overlay。
/// 窗口尺寸持久化到 IPreferencesStore（ui.winW/winH），跨重启恢复；Trace 窗经 account flyout 打开。
/// </summary>
public partial class WorkbenchWindow : Window
{
    private WorkbenchShellViewModel? _vm;

    /// <summary>aurora 漂移定时器（~30fps UI 线程 tick）。IsDreamLayerVisible 变 true 时建（启 3 颗漂移光斑），
    /// 变 false / 窗口关闭时 Stop（停跑省 CPU，避免隐藏元素上动画空转）。</summary>
    /// <remarks>刻意不用 <c>Animation.RunAsync(TranslateTransform,…)</c>——Avalonia 12.1.1 的 TransformAnimator
    /// 会把 target cast 成 <c>Avalonia.Visual</c>，而 TranslateTransform 是 Animatable 非 Visual → InvalidCastException。
    /// 手动 timer 直赋 <c>tf.X/Y</c> 绕开 animator 选择，可靠可调试（同 CLAUDE.md「错路径能编过但运行时抛 cast」教训）。</remarks>
    private DispatcherTimer? _auroraTimer;
    private List<(TranslateTransform Tf, double Drift, double DurX, double DurY)>? _auroraBlobs;
    private long _auroraStartMs;

    /// <summary>桌宠悬浮窗（懒建，首次开启时 new）。null=未开启。</summary>
    private PetWindow? _pet;

    /// <summary>真退出标志：X（关闭）被拦截为「最小化到桌宠」；只有显式「退出」路径置 true 才真正关闭。</summary>
    private bool _isReallyExiting;

    public WorkbenchWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RestoreWindowSize(); // 构造期同步恢复，窗口尚未显示 → 不闪烁
    }

    private static IPreferencesStore? PreferencesStore => DesktopApp.Services.GetService<IPreferencesStore>();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as WorkbenchShellViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 模态窗口 owner 解析：主窗可见 → 主窗；桌宠模式（主窗已 Hide，桌宠 Show）→ 桌宠。
        // 供 ApprovalDialogService 等取可见 owner，避免 ShowDialog(已 Hide 的主窗) 抛
        // "Cannot show window with non-visible owner"（桌宠模式下等审批即踩此坑）。
        DesktopApp.ModalOwnerResolver = () =>
            IsVisible ? this : (_pet is { IsVisible: true } ? _pet : null);
        // 初始主题应用（若有自定义 JSON）。
        ThemeLoader.Apply(this, _vm?.CustomThemeJson);
        ApplyThemeVariant();
        ApplyChrome();   // borderless 皮肤（dream）开自绘标题栏；VM 已据 chrome.borderless 设好 IsBorderless
        UpdateMaxIcon(); // 据当前 WindowState 显最大化/还原图标
        // Avalonia 12.1.1 无 StateChanged/WindowStateChanged 事件；WindowState 是 DirectProperty，
        // 经 AvaloniaObject.PropertyChanged 通用事件过滤 WindowStateProperty 捕获最大化↔还原（覆盖按钮/双击/OS snap 全路径）。
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateMaxIcon(); };
        // dream 类皮肤开 aurora 动画背景层（VM 在窗口打开前已据主题 animated 字段设好 IsDreamLayerVisible）。
        if (_vm?.IsDreamLayerVisible == true) StartAurora();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // X（关闭）拦截为「最小化到桌宠」——桌宠是主窗的最小化驻留形态。真退出（_isReallyExiting）才放行关闭。
        if (!_isReallyExiting)
        {
            e.Cancel = true;
            EnterPetMode();
            return;
        }
        base.OnClosing(e);
        StopAurora();      // 取消漂移动画（免关窗后空转）
        PersistWindowSize();
        _pet?.Close();
    }

    /// <summary>进入桌宠形态：Hide 主窗（从屏幕消失，不依赖 OS 迷你化协议——WindowState=Minimized 在
    /// Avalonia 12.1.1/macOS 上不可靠）+ 显式显示桌宠。桌宠 = 主窗隐藏在桌面的驻留形态。</summary>
    private void EnterPetMode()
    {
        EnsurePet();
        Hide();
        if (!_pet!.IsVisible) _pet.Show();
    }

    /// <summary>从桌宠回到主界面：隐藏桌宠 + 还原主窗（Show+Activate）。</summary>
    public void RestoreFromPet()
    {
        _pet?.Hide();
        Show();
        Activate();
    }

    /// <summary>懒建桌宠（含聊天联动订阅 + 退出回调 + 状态流路由接线）。</summary>
    private void EnsurePet()
    {
        if (_pet is not null) return;
        _pet = new PetWindow { OwnerWindow = this, ExitRequested = ExitApplication, RestoreRequested = RestoreFromPet };
        _pet.Closed += (_, _) =>
        {
            // 桌宠关闭：清空路由器目标（聊天流回无通知），再置 null。PetWindow 自身实现 IRunProgressSink + IPetTokenStream。
            DesktopApp.Services.GetService<Acme.Desktop.Shared.Hosting.PetStatusFeedRouter>()?.SetTarget(null);
            _pet = null;
        };
        // 把活的桌宠指向常驻路由器——ChatWorkspaceViewModel 已解析的路由器实例从此刻起把关键节点 + 逐字流喂给它。
        DesktopApp.Services.GetService<Acme.Desktop.Shared.Hosting.PetStatusFeedRouter>()?.SetTarget(_pet);
        if (_vm?.Chat is { } chat)
            chat.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatWorkspaceViewModel.IsAgentRunning))
                    _pet?.SetThinking(chat.IsAgentRunning);
            };
    }

    /// <summary>真正退出程序：置真退出标志（绕过 X→最小化拦截）→ Close。</summary>
    public void ExitApplication()
    {
        _isReallyExiting = true;
        Close();
    }

    /// <summary>打开运行 Trace 查看窗口（OTel agent.run span）。</summary>
    public void OnOpenTrace(object? sender, RoutedEventArgs e)
    {
        if (_vm is { Trace: { } trace })
        {
            var window = new TraceWindow { DataContext = trace };
            window.Show();
        }
    }

    /// <summary>账号菜单「桌宠」：立即 Hide 主窗 → 桌宠（等同点 X 的行为）。双击桌宠还原主窗。
    /// 桌宠订阅 <see cref="ChatWorkspaceViewModel.IsAgentRunning"/>——agent 运行时切思考态+气泡。</summary>
    public void OnTogglePet(object? sender, RoutedEventArgs e) => EnterPetMode();

    /// <summary>账号菜单「🎨 主题」子项点击：MenuItem.Click 从叶子子项冒泡到父「🎨 主题」，
    /// <c>e.Source</c> 的 DataContext 是被点的 ThemeOption。父项自身 DataContext 是 shell VM（非 ThemeOption）
    /// → 自动跳过。经 ThemeOption.SelectCommand 触发换肤（避开 MenuItem 容器 Command 绑定的跨 popup 歧义）。</summary>
    public void OnSelectTheme(object? sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem mi && mi.DataContext is ThemeOption t)
            t.SelectCommand?.Execute(t.Name);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchShellViewModel.Theme)
            or nameof(WorkbenchShellViewModel.CustomThemeJson))
        {
            ThemeLoader.Apply(this, _vm?.CustomThemeJson);
        }
        if (e.PropertyName == nameof(WorkbenchShellViewModel.IsDarkVariant))
            ApplyThemeVariant();
        if (e.PropertyName == nameof(WorkbenchShellViewModel.IsDreamLayerVisible))
        {
            // 切到/切出 dream 类皮肤：启停 aurora 漂移光斑（CTS 兜底，隐藏即停跑省 CPU）。
            if (_vm?.IsDreamLayerVisible == true) StartAurora(); else StopAurora();
        }
        if (e.PropertyName is nameof(WorkbenchShellViewModel.IsBorderless)
            or nameof(WorkbenchShellViewModel.TitleBarHeight))
        {
            // 切到/切出 borderless 皮肤（dream）或改标题栏高度 → 重应用 chrome。
            ApplyChrome();
        }
    }

    // —— 自绘标题栏 / 无边框 chrome（dream 类皮肤 borderless=true 启用）——

    /// <summary>按 VM 的 IsBorderless 切窗口 chrome。Avalonia 12.1.1 的 chrome API 是 <see cref="WindowDecorations"/>
    /// （替了已删的 <c>ExtendClientAreaChromeHints</c> 枚举 / 标废弃的 <c>SystemDecorations</c>）。
    /// borderless 皮肤（dream）→ <see cref="global::Avalonia.Controls.WindowDecorations.BorderOnly"/>：去系统标题栏
    /// （自绘 CustomTitleBar 接管 min/max/close + 拖拽 + 双击最大化）但保留边框缩放（免丢 resize——主窗常需改尺寸）。
    /// 系统原生 skin（light/dark）→ <see cref="global::Avalonia.Controls.WindowDecorations.Full"/>（OS 标题栏/交通灯）。
    /// 注：不用 <c>ExtendClientAreaToDecorationsHint</c>——那是"内容延伸进保留的标题栏区"的 VSCode 式半 chrome；
    /// 本皮肤走全自绘标题栏，其视觉高度由 XAML 绑 <c>TitleBarHeight</c> 控制，无需 OS 标题栏高度提示。
    /// 自绘标题栏（CustomTitleBar）的显隐已由 XAML 绑 IsBorderless。</summary>
    private void ApplyChrome()
    {
        var borderless = _vm?.IsBorderless ?? false;
        WindowDecorations = borderless ? global::Avalonia.Controls.WindowDecorations.BorderOnly
                                       : global::Avalonia.Controls.WindowDecorations.Full;
    }

    /// <summary>标题栏拖拽：左键按下 BeginMoveDrag（OS 接管移动）。点中按钮时不拖（按钮自处理 Pressed）。</summary>
    public void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;            // 点中窗口按钮 → 让按钮处理，不拖
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);                       // 异步 OS 移动循环；不当 await
    }

    /// <summary>标题栏双击：最大化/还原切换（Windows/桌面惯例）。Avalonia 的 DoubleTapped 复用 TappedEventArgs（无独立 DoubleTappedEventArgs 类型）。</summary>
    public void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>最小化（自绘标题栏按钮）：系统原生最小化到 Dock，不联动桌宠。
    /// 主流桌宠只拦「关闭」不拦「最小化」——最小化语义是「暂时收起稍后还用」，关闭才是「缩到桌宠驻留」。
    /// macOS 黄色交通灯本就走的 OS 原生最小化（不经过此 handler），此处自绘按钮恢复同语义。</summary>
    public void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    public void OnMaximizeRestore(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>据 WindowState 显隐最大化/还原图标（ChromeMaximize / ChromeRestore 互斥）。</summary>
    private void UpdateMaxIcon()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (MaxIcon is not null) MaxIcon.IsVisible = !maximized;
        if (RestoreIcon is not null) RestoreIcon.IsVisible = maximized;
    }

    /// <summary>把主题明暗倾向同步到 Avalonia 原生控件主题。<see cref="ThemeLoader"/> 的 JSON overlay
    /// 只覆盖自定义 token 画刷；原生控件（TextBox/ComboBox/滚动条/ListBox…）须经 <see cref="ThemeVariant"/>
    /// 翻转，否则 dark 主题下原生控件仍是浅底白块。设到 Application 级——所有子窗口（设置/Trace…）一并翻转。</summary>
    private void ApplyThemeVariant()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = (_vm?.IsDarkVariant ?? false) ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // —— 窗口尺寸持久化（布局/分隔条由 WorkspaceView + WorkbenchShellViewModel 跨重启持久） ——

    /// <summary>构造期同步恢复窗口尺寸（JsonPreferencesStore.GetAsync 同步完成）。</summary>
    private void RestoreWindowSize()
    {
        var store = PreferencesStore;
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("ui.winW").GetAwaiter().GetResult(), out var w) && w >= 900)
                Width = w;
            if (double.TryParse(store.GetAsync("ui.winH").GetAwaiter().GetResult(), out var h) && h >= 600)
                Height = h;
        }
        catch { /* 读取失败用默认尺寸 */ }
    }

    /// <summary>关闭时持久化窗口尺寸。fire-and-forget 异步写（不阻塞 UI 线程——之前的 Task.Run().Wait()
    /// 在退出路径上同步阻塞等磁盘 IO，是退出卡顿根因）。窗口尺寸属低价值数据，极端情况丢写仅是下次用默认尺寸。</summary>
    private void PersistWindowSize()
    {
        var store = PreferencesStore;
        if (store is null) return;
        double winW = Width, winH = Height;
        try
        {
            _ = Task.Run(async () =>
            {
                await store.SetAsync("ui.winW", winW.ToString("0.#"));
                await store.SetAsync("ui.winH", winH.ToString("0.#"));
            });
        }
        catch { /* 关闭期写入失败忽略 */ }
    }

    // —— 动画梦幻背景层（aurora 漂移光斑；dream 类皮肤 animated=true 启用）——

    /// <summary>启 aurora：建 3 颗径向渐变光斑（紫/蓝/品红），居中 + TranslateTransform 漂移。
    /// X/Y 周期互质 → 轨迹不重复（有机感）。timer ~30fps 直赋 tf.X/Y 漂移；StopAurora/关窗即停。</summary>
    private void StartAurora()
    {
        if (_auroraTimer is not null) return;       // 已运行（重复触发幂等）
        var layer = DreamLayer;
        if (layer is null) return;

        // 光斑：(颜色 含 alpha, 直径, 漂移幅度 px, X 周期 s, Y 周期 s)
        var specs = new (Color Color, double Size, double Drift, double DurX, double DurY)[]
        {
            (Color.FromArgb(0x55, 0xa7, 0x8b, 0xfa), 680, 150, 24, 31), // 紫
            (Color.FromArgb(0x44, 0x60, 0xa5, 0xfa), 600, 130, 29, 37), // 蓝
            (Color.FromArgb(0x33, 0xd9, 0x46, 0xef), 560, 110, 26, 33), // 品红
        };
        _auroraBlobs = new List<(TranslateTransform, double, double, double)>(specs.Length);
        foreach (var (color, size, drift, durX, durY) in specs)
        {
            var tf = new TranslateTransform();
            var blob = new Ellipse
            {
                Width = size, Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = MakeRadialGlow(color),
                RenderTransform = tf,
            };
            layer.Children.Add(blob);
            _auroraBlobs.Add((tf, drift, durX, durY));
        }
        _auroraStartMs = Environment.TickCount64;
        _auroraTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _auroraTimer.Tick += OnAuroraTick;
        _auroraTimer.Start();
    }

    /// <summary>aurora tick：按已运行秒数算各光斑 X/Y 正弦漂移，直赋 TranslateTransform。绕开 Animation API。</summary>
    private void OnAuroraTick(object? sender, EventArgs e)
    {
        if (_auroraBlobs is null) return;
        var t = (Environment.TickCount64 - _auroraStartMs) / 1000.0;
        const double twoPi = 2.0 * Math.PI;
        foreach (var (tf, drift, durX, durY) in _auroraBlobs)
        {
            tf.X = Math.Sin(t * twoPi / durX) * drift;
            tf.Y = Math.Sin(t * twoPi / durY) * drift * 0.7;
        }
    }

    /// <summary>停 aurora：Stop timer（漂移随停）+ 清空 DreamLayer 子控件。</summary>
    private void StopAurora()
    {
        if (_auroraTimer is null) return;
        _auroraTimer.Stop();
        _auroraTimer.Tick -= OnAuroraTick;
        _auroraTimer = null;
        _auroraBlobs = null;
        DreamLayer?.Children.Clear();
    }

    /// <summary>径向渐变光斑画刷：中心 = 有色 alpha → 边缘透明（同色 alpha 0）。软发光，免 BlurEffect。</summary>
    private static IBrush MakeRadialGlow(Color c)
    {
        var rg = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        };
        rg.GradientStops.Add(new GradientStop { Color = c, Offset = 0 });
        rg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1 });
        return rg;
    }
}
