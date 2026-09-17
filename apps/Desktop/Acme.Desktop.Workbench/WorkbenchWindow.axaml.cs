using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Shared.Telemetry;
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

    /// <summary>最大化前保存的还原帧（Width/Height/Position）。macOS 的 borderless 窗口（WindowDecorations=BorderOnly）
    /// 不干净支持 WindowState.Maximized↔Normal 往返——还原时 OS 不记录最大化前的 frame，导致窗口回到错尺寸、
    /// 内部 Grid/DockPanel 布局按最大化边界测量后错乱。故最大化/还原改走手动 frame 快照：进最大化前存 <see cref="_restoreFrame"/>，
    /// 还原时写回。null = 当前处于还原态（非最大化）。</summary>
    private (double Width, double Height, PixelPoint Position)? _restoreFrame;

    public WorkbenchWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RestoreWindowSize(); // 构造期同步恢复，窗口尚未显示 → 不闪烁
    }

    private static IPreferencesStore? PreferencesStore => DesktopApp.TryGetService<IPreferencesStore>();

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
        ApplyPlatformChromeButtons(); // macOS 显示左上交通灯、非 macOS 显示右上三键
        UpdateMaxIcon(); // 据当前 WindowState 显最大化/还原图标
        // 注册右下角 toast 宿主（ToastService 单例在此叠加气泡；窗口打开才有可视树）。
        (DesktopApp.Services?.GetService<IToastService>() as ToastService)?.AttachHost(ToastHost);
        // Avalonia 12.1.1 无 StateChanged/WindowStateChanged 事件；WindowState 是 DirectProperty，
        // 经 AvaloniaObject.PropertyChanged 通用事件过滤 WindowStateProperty 捕获最大化↔还原（覆盖按钮/双击/OS snap 全路径）。
        // 最小化 → 进桌宠：黄色交通灯 / Cmd+M / 自绘按钮都会把 WindowState 置 Minimized，捕获后转成
        // EnterPetMode（Hide 主窗 + 显示桌宠）而非缩到 Dock——「最小化=进桌宠」语义，Dock 不驻留。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateMaxIcon();
                if (WindowState == WindowState.Minimized)
                    OnTrueMinimized();
            }
        };
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
        // dock Quit 时容器已先于关窗释放——走容错解析（TryGetService），不抛 ObjectDisposedException。
        (DesktopApp.TryGetService<IToastService>() as ToastService)?.DetachHost();
        StopAurora();      // 取消漂移动画（免关窗后空转）
        PersistWindowSize();
        // 不再在此同步 _pet.Close()——真退出路径在 ExitApplication 里已先关桌宠（见下）。此处失序/重入
        // 关桌宠会与其自身 ContextMenu/Click handler 仍在栈上冲突，是退出卡死根因之一。
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

    /// <summary>真正退出程序：置真退出标志（绕过 X→最小化拦截）→ 先关桌宠再关主窗。
    /// 经 Dispatcher.UIThread.Post 延迟一帧执行——桌宠右键「退出程序」是在桌宠自身的
    /// MenuItem.Click handler 里调本方法，若在此同步 Close 会在桌宠窗口自己的 handler 栈上
    /// 重入关闭（ContextMenu popup 仍开），配合 ShutdownRequested 的同步释放阻塞 UI 线程 → 退出卡死。
    /// 延后到 handler 弹回后跑：先关桌宠从视觉树摘除，再关主窗触发 Avalonia 收尾。</summary>
    public void ExitApplication()
    {
        _isReallyExiting = true;
        // 崩溃遥测墓碑：干净退出标记（幂等；与 PrepareForShutdown 构成最小完备集，未标记的下次启动合成 UncleanShutdown）。
        CrashRecorder.MarkCleanShutdown();
        // 取消所有后台 agent run（同 PrepareForShutdown——两个真退出路径都需）。
        try { _vm?.Chat?.CancelAllRunning(); }
        catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
        Dispatcher.UIThread.Post(() =>
        {
            _pet?.Close(); // 先关桌宠（其 OnClosed 停动画定时器）
            Close();       // 再关主窗——OnClosing 见 _isReallyExiting=true 直接放行
        });
    }

    /// <summary>系统级退出（macOS dock 右键 Quit / Cmd+Q 等 <see cref="IClassicDesktopStyleApplicationLifetime.ShutdownRequested"/>）
    /// 前的准备：置真退出标志 + 关桌宠，使 <see cref="OnClosing"/> 放行而非拦截成「最小化到桌宠」。
    /// dock Quit 不经 <see cref="ExitApplication"/>——若不调此方法，OnClosing 见 _isReallyExiting=false 会
    /// e.Cancel=true + EnterPetMode 拦截系统退出，与 macOS terminate 流程冲突 → 退出崩溃（虽最终退出）。
    /// 在 ShutdownRequested 开头调（早于 lifetime 关窗口）；dock 操作时 PetWindow 无活跃 handler（非桌宠菜单点击），
    /// 故 _pet.Close() 不重入栈（区别 ExitApplication 需 Dispatcher.Post 延迟避开桌宠自身 handler 栈）。</summary>
    public void PrepareForShutdown()
    {
        _isReallyExiting = true;
        // 崩溃遥测墓碑：干净退出标记（dock Quit / Cmd+Q 全走这里，幂等）。
        CrashRecorder.MarkCleanShutdown();
        // 取消所有会话的后台 agent run（多会话并发改造后，退出时各 runtime 可能仍在跑——
        // Cancel 让 agent 工具调用抛 OCE，防退出瞬间背景 run 执行危险工具的孤儿副作用）。
        try { _vm?.Chat?.CancelAllRunning(); }
        catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
        try { _pet?.Close(); }
        catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
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
        ToggleMaximizeRestore();
    }

    /// <summary>最小化（自绘标题栏按钮）：进桌宠，不缩到 Dock。与 X（关闭）同语义——
    /// 「最小化=进桌宠」：主窗 Hide + 桌宠驻留。直接调 EnterPetMode，绕过 WindowState=Minimized
    /// （否则会额外触发一次 OnTrueMinimized，且留下一个已 Minimized 的隐藏窗口状态不干净）。</summary>
    public void OnMinimize(object? sender, RoutedEventArgs e) => EnterPetMode();

    /// <summary>系统级最小化落到 WindowState=Minimized 后的统一处理：转成桌宠驻留。
    /// 默认皮肤已全自绘标题栏（light/dark 也开了 chrome.borderless），黄色交通灯不再存在，主路径是
    /// 自绘最小化按钮 <see cref="OnMinimize"/> 直接进桌宠。此兜底覆盖 Cmd+M 等 OS 级最小化快捷键——
    /// macOS 最小化是异步 OS 动画，事件到达时动画可能仍在进行，若此刻同步 Hide()+复位 WindowState 会与
    /// surface 竞态留下「内容已卸载、native shell 还在」的空壳。故：①先复位 Normal（顺序在 Hide 之前）；
    /// ②延迟一帧再 EnterPetMode（让最小化动画先落地，避免时序打架）。</summary>
    private void OnTrueMinimized()
    {
        // 桌宠驻留时（主窗已 Hide）也可能收到 PropertyChanged 噪声，避免重复进。
        if (!IsVisible || _pet is { IsVisible: true }) return;
        // ① 先复位：必须在 Hide 之前把 Minimized 态清掉，否则 Hide 后设 WindowState 会把已卸载内容的
        //    native surface 重新拉回成一个空壳（内容已离屏、壳还在屏幕）。
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // ② 延迟一帧再进桌宠：让最小化动画先收尾，Hide 不与 OS minimize surface 竞态。
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return; // 期间可能已被其它路径关闭/恢复，不再重复进
            EnterPetMode();
        });
    }

    public void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    public void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>最大化/还原切换。borderless 皮肤（chrome.borderless=true，含内置 light/dark/dream 全量）在 macOS 上
    /// 用 WindowState.Maximized 走 OS 原生最大化→还原时，OS 不记录最大化前的 frame，导致还原后窗口尺寸/布局错乱
    /// （内部 Grid/DockPanel 按最大化边界测量）。故改手动 frame 快照：进最大化前存 Width/Height/Position，还原时写回，
    /// 最大化的目标帧取当前所在屏幕的 WorkingArea（等价 OS 最大化，但完全自控、还原可靠）。
    /// 系统原生 chrome（borderless=false）仍走 WindowState（OS 标题栏/交通灯自己管 frame，往返正常）。</summary>
    private void ToggleMaximizeRestore()
    {
        if (_vm?.IsBorderless != true)
        {
            // 原生 chrome：OS 自己管最大化 frame，走 WindowState 即可。
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (_restoreFrame is null)
        {
            // —— 进入最大化：快照当前 frame，再铺满当前屏幕 WorkingArea ——
            _restoreFrame = (Width, Height, Position);
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var wa = screen.WorkingArea;
                Position = wa.Position;
                Width = wa.Width;
                Height = wa.Height;
            }
        }
        else
        {
            // —— 还原：写回快照 frame ——
            var f = _restoreFrame.Value;
            Width = f.Width;
            Height = f.Height;
            Position = f.Position;
            _restoreFrame = null;
        }
        UpdateMaxIcon();
    }

    /// <summary>据当前是否最大化显隐最大化/还原图标（ChromeMaximize / ChromeRestore 互斥）。
    /// borderless 皮肤手动管 frame（不碰 WindowState，见 <see cref="ToggleMaximizeRestore"/>），最大化态由
    /// <see cref="_restoreFrame"/> 非空判定；原生 chrome 仍读 WindowState（OS 自己管 frame）。</summary>
    private void UpdateMaxIcon()
    {
        var maximized = IsMaximized;
        if (MaxIcon is not null) MaxIcon.IsVisible = !maximized;
        if (RestoreIcon is not null) RestoreIcon.IsVisible = maximized;
        ApplyMacMaxGlyph();
    }

    /// <summary>当前是否处于最大化态。borderless 皮肤由 <see cref="_restoreFrame"/> 非空判定（手动 frame，不依赖
    /// WindowState）；原生 chrome 读 WindowState。</summary>
    private bool IsMaximized => _vm?.IsBorderless == true ? _restoreFrame.HasValue : WindowState == WindowState.Maximized;

    /// <summary>mac 交通灯绿点（最大化）hover 时显 ⤢（最大化）或 ⤡（还原）。绿点符号随最大化态切换，
    /// 与 WinButtons 的 MaxIcon/RestoreIcon 同源。仅在 macOS 生效（MacTrafficLights 可见时）。</summary>
    private void ApplyMacMaxGlyph()
    {
        if (MaxGlyph is null) return;
        var maximized = IsMaximized;
        MaxGlyph.Data = Geometry.Parse(maximized ? Codicons.ChromeRestore : Codicons.ChromeMaximize);
    }

    /// <summary>按平台切换标题栏按钮组：macOS 显示左上角交通灯（红黄绿圆点），非 macOS 显示右上角
    /// Windows 式三键。交通灯的绿点（最大化）在 hover 时显 ⤢，且须随 WindowState 切「最大化/还原」符号——
    /// 但 mac 交通灯圆点内符号 hover 才显，非 hover 恒圆点，故绿点符号用 <see cref="UpdateMacMaxGlyph"/> 单独维护。</summary>
    private void ApplyPlatformChromeButtons()
    {
        var isMac = OperatingSystem.IsMacOS();
        if (MacTrafficLights is not null) MacTrafficLights.IsVisible = isMac;
        if (WinButtons is not null) WinButtons.IsVisible = !isMac;
        ApplyMacMaxGlyph();
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
