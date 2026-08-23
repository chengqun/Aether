using System;
using System.Text;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Aether.AI.Core.Agents;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌宠（悬浮宠物）：无边框透明底置顶小窗。形象层 = <see cref="LottiePetControl"/>（Skottie 渲染 Lottie 动画）。
/// P0 悬浮/置顶/拖拽；P1 点击气泡说话 + 右键菜单（置顶开关/回到主界面/退出程序）；
/// P2 与聊天联动——WorkbenchWindow 订阅 Chat.IsAgentRunning 调 <see cref="SetThinking"/>，agent 运行时切「思考」mood；
/// P3 实时反馈镜——实现 <see cref="IRunProgressSink"/>（关键节点 → 气泡 + mood）+ <see cref="IPetTokenStream"/>
/// （逐字答案打字机 + 说话 mood）。关键节点走 Core 的 <see cref="RunProgress"/>（与手机端同源），
/// 逐字 token 是桌宠专属细粒度通道——两者分层。语义状态经 <see cref="LottiePetControl.SetMood"/> 映射为
/// <see cref="PetMood"/>（思考/工作/说话/待机）驱动动画节奏与脉冲。
/// </summary>
public partial class PetWindow : Window, IRunProgressSink, IPetTokenStream
{
    private readonly string[] _idleLines =
    {
        "你好呀～", "有什么要我帮忙的吗？", "我在哦。",
        "今天也要加油呀！", "(伸个懒腰)…喵～", "盯……",
    };

    private Random? _rand;

    // —— P3 实时反馈镜：关键节点 → 造型 + 气泡；逐字 token → 气泡打字机 ——
    private readonly StringBuilder _answerBuf = new();     // 本轮回答累积（逐字流，节流渲染进气泡）
    private DispatcherTimer? _bubbleTimer;                  // 气泡内容节流 flush（50ms，与主窗同节奏）
    private IDisposable? _bubbleAutoHide;                   // 气泡自动收起（工具/关键节点句 2.5s；流式回答持续到本轮结束）
    // 注：DispatcherTimer.RunOnce 返回 IDisposable（非 DispatcherTimer），故 _bubbleAutoHide 用 IDisposable。

    /// <summary>主界面窗口引用。桌宠是 Topmost 浮窗，被点击会被 macOS 激活成 key window、
    /// 把主窗压到后台——为使「点击桌宠后仍能切回主界面」，桌宠被激活时立即把焦点还给主窗。</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>「退出程序」回调（由宿主 WorkbenchWindow 注入 = 真退出）。桌宠在主窗最小化时驻留，
    /// 是唯一还能交互的窗口——须经此回调才能真正退出整个程序（否则无从退出）。</summary>
    public Action? ExitRequested { get; set; }

    /// <summary>「回到主界面」回调（宿主注入 = WorkbenchWindow.RestoreFromPet）。双击/右键菜单走它。</summary>
    public Action? RestoreRequested { get; set; }

    // 偏好（位置持久化）：懒解析，设计期 DesktopApp.Services 未就绪则为 null。
    private readonly IPreferencesStore? _prefs = DesktopApp.Services.GetService<IPreferencesStore>();

    // 表现引擎抽象（Avatar Runtime）：PetWindow 的运行时状态控制只依赖此接口，
    // 替换 Lottie → Spine/Live2D 只动 OnOpened 里这一处 setup，信号链零改动。
    private IAvatarEngine? _avatar;

    // 位置持久化：Window.Position 是普通 CLR 属性（无 PositionProperty 静态字段，Avalonia 12 无 Moved 事件），
    // 无法经 PropertyChanged 监听 → 用低频定时器对比检测位置变化的后保存（开销可忽略）。
    private DispatcherTimer? _posWatch;
    private PixelPoint? _lastPos;

    public PetWindow()
    {
        InitializeComponent();
        _rand = new Random();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 初始定位：有保存的拖拽位置 → 恢复；否则 → 主屏右上角。
        RestoreOrDefaultPosition();
        _lastPos = Position;
        _posWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posWatch.Tick += (_, _) =>
        {
            if (_lastPos is { } lp && lp != Position)
            {
                _lastPos = Position;
                PersistPosition();
            }
        };
        _posWatch.Start();

        // setup：Lottie 引擎加载全部 mood 动画（Assets/pet/{idle,thinking,working,speaking}.json，缺省回退 idle）。
        PetLottie?.LoadAllFromAssets();
        _avatar = PetLottie as IAvatarEngine;
        _avatar?.SetMood(PetMood.Idle);

        var menu = new ContextMenu();

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
        // 拖窗时窗口随指针一起动，窗口内坐标几乎不变 → 用指针位移判断「点击 vs 拖拽」不可靠。
        // 点击（几乎没拖）就 Say 一句；位置保存由 _posWatch 定时对比 Position 变化（见 OnOpened）。
        var d = e.GetPosition(this) - _pressPoint;
        if (d.X * d.X + d.Y * d.Y < 16) // <4px
            Say(_idleLines[_rand!.Next(_idleLines.Length)]);
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

    /// <summary>切思考态/待机态。由 WorkbenchWindow 订阅 Chat.IsAgentRunning 变化时调用。true=专注思考造型。</summary>
    public void SetThinking(bool thinking)
        => ApplyKind(thinking ? RunProgressKind.Reasoning : RunProgressKind.RunDone);

    // —— P3：IRunProgressSink（关键节点）+ IPetTokenStream（逐字打字机）实现 ——

    /// <summary>关键节点信号（聊天流 → Core RunProgress → 此处）。UI 线程 marshal + 按 Kind 驱动造型 + 更新气泡。
    /// 宿主 WorkbenchWindow 把 PetWindow 注册为 IRunProgressSink 后，ChatWorkspaceViewModel 直接 Post 到此处。</summary>
    public void Post(RunProgress signal)
    {
        Dispatcher.UIThread.Post(() => ApplyProgress(signal));
    }

    /// <summary>逐字答案 token（桌宠专属打字机；关键节点另走 <see cref="Post"/>）。</summary>
    public void AppendToken(string token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 只保留「滚动尾巴」——最近 ~30 字，让气泡有「在输出」的动感，但绝不驻留整段回答原文。
            // 全文字在气泡里既挤又无信息，用户要看正文回主窗。超过窗口即丢旧字（滚动窗口）。
            _answerBuf.Append(token);
            if (_answerBuf.Length > 30)
                _answerBuf.Remove(0, _answerBuf.Length - 30);
            StartBubbleStream();
            // 经 IAvatarEngine 说话流 → 口型节奏。
            _avatar?.AppendSpeakToken(token);
        });
    }

    /// <summary>本轮答案流结束（flush 残留 + 停打字机，气泡交由终态 Done/Error 收敛）。</summary>
    public void EndAnswer()
    {
        Dispatcher.UIThread.Post(() =>
        {
            FlushAnswer();
            _answerBuf.Clear();
            StopBubbleStream();
            // 说完了回到待机。
            _avatar?.EndSpeak();
        });
    }

    private void ApplyProgress(RunProgress signal)
    {
        switch (signal.Kind)
        {
            case RunProgressKind.Reasoning:
                ApplyKind(RunProgressKind.Reasoning);
                break;

            case RunProgressKind.ToolStarted:
                ApplyKind(RunProgressKind.ToolStarted);
                if (signal.ToolName is not null)
                    // 短句，不带参数（参数在审批时才需要）；6s 超时，避免「正在 X」无后续完成事件时永久驻留。
                    ShowLine($"🔧 {signal.ToolName}", autoHide: true, autoHideMs: 6000);
                break;

            case RunProgressKind.ToolFinished:
                // 完成也短句，不带参数；2.5s 收。连续多工具完成会覆盖前一句，不刷屏。
                if (signal.ToolName is not null)
                    ShowLine($"✅ {signal.ToolName}", autoHide: true);
                break;

            case RunProgressKind.ApprovalRequested:
                ApplyKind(RunProgressKind.ToolStarted);
                // 审批是唯一必须带关键信息的节点：工具名 +（若源头给了）参数都要，且停久一点（8s）等人看。
                ShowLine($"⛔ 需要你确认「{signal.ToolName}」", autoHide: true, autoHideMs: 8000);
                break;

            case RunProgressKind.PlanUpdated:
                ApplyKind(RunProgressKind.ToolStarted);
                ShowLine($"📋 计划 {signal.PlanStepCount} 步", autoHide: true);
                break;

            case RunProgressKind.RunDone:
                // 完成只冒一句固定短话，不显示回答原文/摘要——正文留主窗，桌宠气泡不驻留长段。
                ShowLine("搞定啦 ✨", autoHide: true);
                _avatar?.SetMood(PetMood.Happy);
                break;

            case RunProgressKind.RunError:
                StopBubbleStream();
                ShowLine($"⚠️ 出错了{(string.IsNullOrEmpty(signal.Text) ? "" : "：" + Truncate(signal.Text, 40))}", autoHide: true);
                _avatar?.SetMood(PetMood.Error);
                break;
        }
    }

    /// <summary>按语义状态切换形象 mood（思考/工作/待机）。</summary>
    private void ApplyKind(RunProgressKind kind)
    {
        _avatar?.SetMood(kind switch
        {
            RunProgressKind.Reasoning => PetMood.Thinking,
            RunProgressKind.ToolStarted
                or RunProgressKind.ApprovalRequested
                or RunProgressKind.PlanUpdated => PetMood.Working,
            _ => PetMood.Idle, // ToolFinished / 其它 → 回待机
        });
    }

    /// <summary>气泡内容节流：启动 50ms flush 定时器，把 _answerBuf 累积文本写进气泡（打字机效果）。
    /// 与主窗 flushTimer 同节奏（50ms），避免逐 token 重排。</summary>
    private void StartBubbleStream()
    {
        if (_bubbleTimer is null && SpeechBubble is not null && SpeechText is not null)
        {
            _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _bubbleTimer.Tick += (_, _) => FlushAnswer();
            _bubbleTimer.Start();
        }
        if (SpeechBubble is not null) SpeechBubble.IsVisible = true;
    }

    private void FlushAnswer()
    {
        if (SpeechBubble is null || SpeechText is null) return;
        if (_answerBuf.Length > 0)
        {
            SpeechText.Text = _answerBuf.ToString();
            SpeechBubble.IsVisible = true;
        }
    }

    private void StopBubbleStream()
    {
        _bubbleTimer?.Stop();
        _bubbleTimer = null;
    }

    /// <summary>气泡显一句短话（关键节点/工具句——autoHide=true 按 <paramref name="autoHideMs"/> 自动收；false 持续到下一句覆盖）。
    /// 文本一律短句——桌宠气泡不驻留长段（工具名/参数/错误全文都截短，正文回主窗看）。</summary>
    private void ShowLine(string line, bool autoHide, int autoHideMs = 2500)
    {
        if (SpeechBubble is null || SpeechText is null) return;
        SpeechText.Text = line;
        SpeechBubble.IsVisible = true;
        _bubbleAutoHide?.Dispose();
        _bubbleAutoHide = null;
        if (autoHide)
            _bubbleAutoHide = DispatcherTimer.RunOnce(
                () => { if (SpeechBubble is not null) SpeechBubble.IsVisible = false; },
                TimeSpan.FromMilliseconds(autoHideMs));
    }

    /// <summary>截短文本到 <paramref name="max"/> 字符（超长加省略号）。桌宠气泡绝不驻留长段，错误/参数等一律截短。</summary>
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > max ? text[..max] + "…" : text;
    }

    // —— 生命周期 ——

    protected override void OnClosed(EventArgs e)
    {
        _posWatch?.Stop();
        _posWatch = null;
        StopBubbleStream();
        _bubbleAutoHide?.Dispose();
        _avatar?.StopAndDispose();   // 释放动画 + 定时器 + 位图（替换引擎时同路径清理）
        base.OnClosed(e);
    }
}
