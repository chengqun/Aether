using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌宠（悬浮宠物）：无边框透明底置顶小窗，emoji 形象飘在桌面上。P0~P2：
/// P0 悬浮/置顶/拖拽/待机动画；P1 点击气泡说话 + 右键菜单（换造型/置顶开关/隐藏）；
/// P2 与聊天联动——由 WorkbenchWindow 订阅 Chat.IsAgentRunning 变化调 <see cref="SetThinking"/>，
/// agent 运行时切「思考中」造型 + 随机气泡。
/// 动画复用 aurora 的成熟先例：DispatcherTimer（~30fps）直赋 TranslateTransform/ScaleTransform 的
/// X/Y/ScaleX/ScaleY，绕开 Avalonia 12.1.1 的 Animation.RunAsync(TranslateTransform) 会抛
/// InvalidCastException 的坑（TransformAnimator 把 Animatable cast 成 Visual；见 CLAUDE.md aurora cast 坑）。
/// </summary>
public partial class PetWindow : Window
{
    private static readonly string[] PetGlyphPool = { "🐱", "🐻", "🐸", "🐤", "🐰", "🐶", "🦊", "🐼", "🐨", "🦉" };
    private readonly string[] _thinkingGlyphs = { "💭", "🤔", "🧠" };
    private readonly string[] _idleLines =
    {
        "你好呀～", "有什么要我帮忙的吗？", "我在哦。",
        "今天也要加油呀！", "(伸个懒腰)…喵～", "盯……",
    };
    private readonly string[] _thinkingLines =
    {
        "让我想想……", "嗯……正在处理。", "稍等，我在算。", "这个有点意思。",
    };

    private int _glyphIndex;
    private bool _isThinking;
    private DispatcherTimer? _animTimer;
    private long _startMs;
    private Random? _rand;

    /// <summary>主界面窗口引用。桌宠是 Topmost 浮窗，被点击会被 macOS 激活成 key window、
    /// 把主窗压到后台——为使「点击桌宠后仍能切回主界面」，桌宠被激活时立即把焦点还给主窗。</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>「退出程序」回调（由宿主 WorkbenchWindow 注入 = 真退出）。桌宠在主窗最小化时驻留，
    /// 是唯一还能交互的窗口——须经此回调才能真正退出整个程序（否则无从退出）。</summary>
    public Action? ExitRequested { get; set; }

    /// <summary>「回到主界面」回调（宿主注入 = WorkbenchWindow.RestoreFromPet）。双击/右键菜单走它。</summary>
    public Action? RestoreRequested { get; set; }

    // 动画 transform（每帧直赋，不重建对象；Transform 非 StyledElement，x:Name 不生成字段，故 C# 持有引用并挂 RenderTransform）
    private readonly TranslateTransform _translate = new();
    private readonly ScaleTransform _scale = new();

    // 偏好（位置持久化）：懒解析，设计期 DesktopApp.Services 未就绪则为 null。
    private readonly IPreferencesStore? _prefs = DesktopApp.Services.GetService<IPreferencesStore>();

    public PetWindow()
    {
        InitializeComponent();
        _rand = new Random();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _animTimer.Tick += OnAnimTick;
        _startMs = Environment.TickCount64;
        _animTimer.Start();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 初始定位：有保存的拖拽位置 → 恢复；否则 → 主屏右上角。
        RestoreOrDefaultPosition();

        // OnOpened 后才挂 RenderTransform（PetGlyph 已 realization），并建右键菜单。
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        PetGlyph.RenderTransform = group;

        var menu = new ContextMenu();
        var change = new MenuItem { Header = "换个造型" };
        change.Click += (_, _) => RandomizeGlyph();
        menu.Items.Add(change);

        var topmost = new MenuItem { Header = $"置顶{(Topmost ? " ✓" : "")}" };
        topmost.Click += (_, _) =>
        {
            Topmost = !Topmost;
            topmost.Header = $"置顶{(Topmost ? " ✓" : "")}";
        };
        menu.Items.Add(topmost);

        menu.Items.Add(new Separator());
        var back = new MenuItem { Header = "回到主界面" };
        back.Click += (_, _) => BackToMain();
        menu.Items.Add(back);

        var quit = new MenuItem { Header = "退出程序" };
        quit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(quit);

        PetBody.ContextMenu = menu;
    }

    // —— 动画（DispatcherTimer 直赋，绕开 Animation API）——

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (PetBody is null) return;
        var t = (Environment.TickCount64 - _startMs) / 1000.0;
        double floatY, breathe;
        if (_isThinking)
        {
            // 思考中：更快抖动 + 上浮 + 明显呼吸
            floatY = -Math.Abs(Math.Sin(t * 6.0)) * 6.0;
            breathe = 1.0 + Math.Sin(t * 8.0) * 0.08;
        }
        else
        {
            floatY = Math.Sin(t * 2.0) * 4.0;
            breathe = 1.0 + Math.Sin(t * 3.0) * 0.04;
        }
        _translate.Y = floatY;
        _scale.ScaleX = breathe;
        _scale.ScaleY = breathe;
    }

    // —— 拖拽 / 点击 ——

    private Point _pressPoint;

    private void OnPetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(this);
            // OS 接管移动（可靠无回弹）；BeginMoveDrag 是异步 OS 移动循环，不当 await。
            BeginMoveDrag(e);
        }
    }

    private void OnPetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 释放时若指针几乎没动 = 点击 → 弹泡；动了 = 拖拽（BeginMoveDrag 已移动窗口）→ 不弹泡 + 保存新位置。
        var d = e.GetPosition(this) - _pressPoint;
        if (d.X * d.X + d.Y * d.Y < 16) // <4px
            Say(_idleLines[_rand!.Next(_idleLines.Length)]);
        else
            PersistPosition();
    }

    // —— 位置持久化 ——

    /// <summary>恢复上次拖拽位置（有保存则用），否则定位主屏右上角（桌宠默认落点）。</summary>
    private void RestoreOrDefaultPosition()
    {
        var saved = _prefs is not null
            ? (_prefs.GetAsync("pet.x").GetAwaiter().GetResult(), _prefs.GetAsync("pet.y").GetAwaiter().GetResult())
            : (null, null);
        if (double.TryParse(saved.Item1, out var x) && double.TryParse(saved.Item2, out var y) && x >= 0 && y >= 0)
        {
            Position = new PixelPoint((int)x, (int)y);
            return;
        }
        // 默认：主屏工作区右上角，留 16px 边距。
        if (Screens.Primary is { } primary)
        {
            var wa = primary.WorkingArea;
            Position = new PixelPoint(wa.X + wa.Width - (int)Width - 16, wa.Y + 16);
        }
    }

    /// <summary>保存当前窗口位置（拖拽结束后 fire-and-forget 写偏好）。</summary>
    private void PersistPosition()
    {
        if (_prefs is null) return;
        var p = Position;
        _ = Task.Run(async () =>
        {
            await _prefs.SetAsync("pet.x", p.X.ToString());
            await _prefs.SetAsync("pet.y", p.Y.ToString());
        });
    }

    /// <summary>双击宠物 → 回主界面（DoubleTapped 复用 TappedEventArgs，无独立类型）。</summary>
    private void OnPetDoubleTapped(object? sender, TappedEventArgs e) => BackToMain();

    /// <summary>回到主界面（双击 + 右键菜单共用）：委托宿主 RestoreRequested（还原主窗 + 隐藏桌宠）。</summary>
    private void BackToMain() => RestoreRequested?.Invoke();

    // —— 说话 ——

    private void Say(string line)
    {
        if (SpeechText is null || SpeechBubble is null) return;
        SpeechText.Text = line;
        SpeechBubble.IsVisible = true;
        DispatcherTimer.RunOnce(
            () => { if (SpeechBubble is not null) SpeechBubble.IsVisible = false; },
            TimeSpan.FromMilliseconds(2500));
    }

    private void RandomizeGlyph()
    {
        _glyphIndex = _rand!.Next(PetGlyphPool.Length);
        PetGlyph.Text = PetGlyphPool[_glyphIndex];
    }

    /// <summary>切思考态/待机态。由 WorkbenchWindow 订阅 Chat.IsAgentRunning 变化时调用。true=专注思考造型。</summary>
    public void SetThinking(bool thinking)
    {
        _isThinking = thinking;
        if (PetGlyph is null) return;
        if (thinking)
        {
            PetGlyph.Text = _thinkingGlyphs[_rand!.Next(_thinkingGlyphs.Length)];
            Say(_thinkingLines[_rand.Next(_thinkingLines.Length)]);
        }
        else
        {
            PetGlyph.Text = PetGlyphPool[_glyphIndex];
        }
    }

    // —— 生命周期 ——

    protected override void OnClosed(EventArgs e)
    {
        _animTimer?.Stop();
        _animTimer = null;
        base.OnClosed(e);
    }
}
