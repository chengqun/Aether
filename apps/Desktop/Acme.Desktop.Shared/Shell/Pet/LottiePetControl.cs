using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌宠语义状态（信号联动用）。不同状态对应不同动画节奏/缩放脉冲/口型，映射自
/// <c>RunProgressKind</c> + 待机 + 说话（token 流）。后续换多素材时，每个 mood 可绑不同 Lottie 文件。
/// </summary>
public enum PetMood
{
    /// <summary>待机：原速循环 + 轻微呼吸。</summary>
    Idle,
    /// <summary>思考：慢速 + 明显上下浮（模型在推理）。</summary>
    Thinking,
    /// <summary>工作中：快速 + 脉冲（执行工具/等审批/计划）。</summary>
    Working,
    /// <summary>说话：口型节奏缩放（逐字 token 驱动）。</summary>
    Speaking,
}

/// <summary>
/// 桌宠形象层：用 Skia 官方 Skottie（Lottie）渲染会动的角色，替换原 emoji &lt;see cref="TextBlock"/&gt;。
/// Lottie 是「软件骨骼 + 矢量路径形变」动画（呼吸/眨眼/转头/口型），观感近似 Live2D 的轻量版，且零新增原生库
/// （libSkiaSharp 已内置 skottie；见 csproj <c>SkiaSharp.Skottie</c> 引用）。
/// 渲染方案：**离屏渲染**——每帧把 Lottie 画进 <see cref="SKBitmap"/>，再 Marshal.Copy 进 <see cref="WriteableBitmap"/>，
/// 最后 <c>context.DrawImage</c> 画普通位图。刻意不走 <c>context.Custom(ICustomDrawOperation)</c> + Skia 租借——
/// 那条路在透明 <c>Topmost</c> 窗口里每帧重合成，实测一直狂闪（右键弹菜单才停），离屏位图绘制是确定性的、零合成抖动。
/// 播放循环用 <see cref="DispatcherTimer"/> ~30fps 驱动 <c>SeekFrameTime</c>，绕开 Avalonia <c>Animation.RunAsync</c>
/// （TransformAnimator 把 Transform cast 成 Visual 的坑——见桌宠/aurora 教训）。
/// </summary>
public class LottiePetControl : Control
{
    private readonly Dictionary<PetMood, Animation> _animations = new();
    private Animation? _active;
    private DispatcherTimer? _timer;
    private TimeSpan _elapsed;
    private DateTime? _lastTick;
    private long _startMs;

    // 语义状态（信号联动）：mood 决定当前动画 + 播放速度/脉冲节奏。
    private PetMood _mood = PetMood.Idle;

    // 离屏渲染与显示位图
    private SKBitmap? _frameBitmap;         // 离屏画布副本（动画内在尺寸）
    private WriteableBitmap? _displayBitmap; // Avalonia display 位图（控件尺寸）
    private int _dw, _dh;                   // display 尺寸（= 控件尺寸）

    /// <summary>
    /// 从 avares:// 资源按约定一次加载全部 mood 动画。约定路径：<c>Assets/pet/{mood}.json</c>
    /// （idle/thinking/working/speaking）。缺某个文件的 mood 回退到 idle（保证永远有动画可播）。
    /// </summary>
    public void LoadAllFromAssets()
    {
        foreach (var mood in (PetMood[])Enum.GetValues(typeof(PetMood)))
        {
            var uri = new Uri($"avares://Acme.Desktop.Shared/Assets/pet/{mood.ToString().ToLowerInvariant()}.json");
            LoadMoodFromUri(mood, uri, fallbackToIdle: mood != PetMood.Idle);
        }
        Activate(_mood);
    }

    /// <summary>为单个 mood 加载动画（bytes）。失败或缺少时回退 idle（若 mood 非 idle）。</summary>
    public void LoadMood(PetMood mood, byte[] jsonBytes)
    {
        try
        {
            using var data = SKData.CreateCopy(jsonBytes);
            _animations[mood] = Animation.Create(data);
            System.Diagnostics.Trace.WriteLine(
                $"[LottiePetControl] {mood} 加载成功 {_animations[mood].Size.Width}x{_animations[mood].Size.Height} dur={_animations[mood].Duration}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[LottiePetControl] {mood} 加载失败: {ex.Message}");
        }
    }

    private void LoadMoodFromUri(PetMood mood, Uri uri, bool fallbackToIdle)
    {
        try
        {
            if (!AssetLoader.Exists(uri))
            {
                if (fallbackToIdle)
                    System.Diagnostics.Trace.WriteLine($"[LottiePetControl] 缺少 {mood} 动画 {uri} → 回退 idle");
                return; // 回退 idle：字典里无该 mood 时 Activate 会落到 idle
            }
            using var s = AssetLoader.Open(uri);
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            LoadMood(mood, ms.ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[LottiePetControl] {mood} 读取失败 {uri}: {ex.Message}");
        }
    }

    /// <summary>播放速度倍率（1=原速）。多动画时各动画自带节奏，此值仅作全局调速。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>是否循环（false=播到结尾停在末帧）。</summary>
    public bool Loop { get; set; } = true;

    public LottiePetControl()
    {
        ClipToBounds = false;
        _startMs = Environment.TickCount64;
    }

    /// <summary>激活指定 mood 的动画（换引用 + 重置播放时间，零重新加载）。缺该 mood 时回退 idle。</summary>
    private void Activate(PetMood mood)
    {
        _mood = mood;
        _active = _animations.TryGetValue(mood, out var a)
            ? a
            : (_animations.TryGetValue(PetMood.Idle, out var idle) ? idle : null);

        _elapsed = TimeSpan.Zero;
        _lastTick = null;
        _startMs = Environment.TickCount64; // 重置脉冲相位

        if (_active is not null && _timer is null) StartTimer();
        InvalidateVisual();
    }

    private void StartTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var anim = _active;
        if (anim is null) return;
        var now = DateTime.UtcNow;
        if (_lastTick is { } last)
        {
            _elapsed += TimeSpan.FromSeconds((now - last).TotalSeconds * EffectiveSpeed);
            if (Loop && anim.Duration > TimeSpan.Zero && _elapsed > anim.Duration)
                _elapsed = TimeSpan.Zero;
            else if (!Loop && _elapsed >= anim.Duration)
                _elapsed = anim.Duration;
        }
        _lastTick = now;
        InvalidateVisual();
    }

    /// <summary>按 mood 的有效播放速度（= 用户 Speed × mood 倍率）。多动画时各动画已设计好节奏，
    /// 仅保留轻量调速：thinking 略慢、working 略快，speaking/idle 原速。</summary>
    private double EffectiveSpeed => _mood switch
    {
        PetMood.Thinking => Speed * 0.85,
        PetMood.Working   => Speed * 1.1,
        _                 => Speed,
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var anim = _active;
        if (anim is null) return;

        int aw = (int)anim.Size.Width;
        int ah = (int)anim.Size.Height;
        if (aw <= 0 || ah <= 0) return;

        int dw = (int)(Width > 0 ? Width : Bounds.Width);
        int dh = (int)(Height > 0 ? Height : Bounds.Height);
        if (dw <= 0 || dh <= 0) return;

        // 1. 离屏渲染 Lottie 当前帧到 display 尺寸的 SKBitmap（透明底，canvas.Scale 缩放动画）。
        EnsureFrameSurface(dw, dh);
        using (var canvas = new SKCanvas(_frameBitmap!))
        {
            canvas.Clear(SKColors.Transparent);
            var sx = (float)dw / aw;
            var sy = (float)dh / ah;
            canvas.Scale(sx, sy);

            // 语义状态动效：围绕中心的脉冲/上下浮（各动画已自带动画，这里仅加轻量呼吸，说话加口型节奏）。
            ApplyMoodMotion(canvas, dw, dh);

            anim.SeekFrameTime(_elapsed, null);
            anim.Render(canvas, new SKRect(0, 0, aw, ah));
        }

        // 2. 复制到 Avalonia WriteableBitmap（同尺寸，逐行拷贝）。
        EnsureDisplayBitmap(dw, dh);
        CopyFrameToDisplay();

        // 3. 画位图（普通 IImage 绘制，零合成抖动）。
        context.DrawImage(_displayBitmap, new Rect(0, 0, dw, dh));
    }

    private void EnsureFrameSurface(int w, int h)
    {
        if (_frameBitmap is null || _frameBitmap.Width != w || _frameBitmap.Height != h)
        {
            _frameBitmap?.Dispose();
            _frameBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        }
    }

    /// <summary>
    /// 按当前 mood 施加缩放脉冲 + 上下浮（围绕控件中心）。缩放相位用 <c>_startMs</c> 保证切状态时重置，
    /// 避免从旧相位跳变。待机=轻微呼吸；思考=慢速大浮；工作=快速小脉冲；说话=中频口型节奏。
    /// </summary>
    private void ApplyMoodMotion(SKCanvas canvas, int dw, int dh)
    {
        var t = (Environment.TickCount64 - _startMs) / 1000.0;
        const double twoPi = 2.0 * Math.PI;

        double pulse;  // 缩放幅度（相对中心）
        double floatY; // 上下浮幅度（像素，动画内在坐标 → 需乘 display 缩放比）
        switch (_mood)
        {
            case PetMood.Thinking:
                pulse = Math.Sin(t * twoPi / 2.6) * 0.06;           // 慢呼吸
                floatY = -Math.Abs(Math.Sin(t * twoPi / 3.2)) * 3.0; // 缓慢上浮
                break;
            case PetMood.Working:
                pulse = Math.Sin(t * twoPi / 0.9) * 0.04;           // 较快脉冲
                floatY = Math.Sin(t * twoPi / 1.3) * 1.2;
                break;
            case PetMood.Speaking:
                pulse = Math.Sin(t * twoPi / 0.35) * 0.025;         // 口型节奏（~3Hz）
                floatY = Math.Sin(t * twoPi / 0.7) * 0.8;
                break;
            default: // Idle
                pulse = Math.Sin(t * twoPi / 3.5) * 0.02;           // 轻微呼吸
                floatY = Math.Sin(t * twoPi / 4.0) * 0.6;
                break;
        }

        // 围绕中心缩放 + 上下浮（display 像素坐标）。
        var cx = dw / 2.0;
        var cy = dh / 2.0;
        var s = 1.0 + pulse;
        canvas.Translate((float)cx, (float)cy);
        canvas.Scale((float)s, (float)s);
        canvas.Translate((float)-cx, (float)(-cy + floatY));
    }

    private void EnsureDisplayBitmap(int w, int h)
    {
        if (_displayBitmap is null || _dw != w || _dh != h)
        {
            _displayBitmap?.Dispose();
            _dw = w; _dh = h;
            _displayBitmap = new WriteableBitmap(
                new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        }
    }

    private void CopyFrameToDisplay()
    {
        if (_frameBitmap is null || _displayBitmap is null) return;
        using var fb = _displayBitmap.Lock();
        var dst = fb.Address;
        var bytes = _frameBitmap.Bytes;          // byte[]（RGBA8888 非预乘，行距=宽*4 无填充）
        var height = Math.Min(_dh, _frameBitmap.Height);
        var rowBytes = fb.RowBytes;
        if (rowBytes == _frameBitmap.RowBytes)
        {
            // 行距一致 → 整块拷贝。
            var total = _frameBitmap.RowBytes * height;
            Marshal.Copy(bytes, 0, dst, total);
        }
        else
        {
            // 行距不一致 → 逐行拷贝。
            var widthBytes = Math.Min(rowBytes, _frameBitmap.RowBytes);
            var row = new byte[widthBytes];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(bytes, _frameBitmap.RowBytes * y, row, 0, widthBytes);
                Marshal.Copy(row, 0, dst + y * rowBytes, widthBytes);
            }
        }
    }

    /// <summary>播放到指定时间（外部驱动口型/切段用）。</summary>
    public void Seek(TimeSpan time)
    {
        _elapsed = time;
        _lastTick = null;
        InvalidateVisual();
    }

    /// <summary>
    /// 切换语义状态（信号联动入口）。换当前动画引用 + 重置播放时间，零重新加载/读盘。
    /// 缺该 mood 动画时经 <see cref="Activate"/> 回退 idle，保证永远有画面。
    /// </summary>
    public void SetMood(PetMood mood)
    {
        if (_mood == mood && _active is not null)
        {
            // Speaking 需持续刷新口型节奏相位，不因同 mood 短路跳过。
            if (mood != PetMood.Speaking) return;
            _startMs = Environment.TickCount64;
            InvalidateVisual();
            return;
        }
        Activate(mood);
    }

    /// <summary>当前语义状态。</summary>
    public PetMood Mood => _mood;

    /// <summary>停播并释放全部动画 + 定时器 + 位图（窗口关闭/换形象时调）。</summary>
    public void StopAndDispose()
    {
        StopTimer();
        foreach (var a in _animations.Values) a.Dispose();
        _animations.Clear();
        _active = null;
        _frameBitmap?.Dispose();
        _frameBitmap = null;
        _displayBitmap?.Dispose();
        _displayBitmap = null;
    }
}
