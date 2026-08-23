using System;
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
    private Animation? _animation;
    private DispatcherTimer? _timer;
    private TimeSpan _elapsed;
    private DateTime? _lastTick;
    private long _startMs;

    // 语义状态（信号联动）：mood 驱动播放速度 + 缩放/位置脉冲；Pulse 由外部信号触发（说话/完成/出错）。
    private PetMood _mood = PetMood.Idle;

    // 离屏渲染与显示位图
    private SKBitmap? _frameBitmap;         // 离屏画布副本（动画内在尺寸）
    private WriteableBitmap? _displayBitmap; // Avalonia display 位图（控件尺寸）
    private int _aw, _ah;                   // 动画内在尺寸
    private int _dw, _dh;                   // display 尺寸（= 控件尺寸）

    /// <summary>动画文件内容（Lottie json 字节）。set 时（重新）加载并启动循环播放。</summary>
    private byte[]? _jsonBytes;
    public byte[]? JsonBytes
    {
        get => _jsonBytes;
        set
        {
            _jsonBytes = value;
            _sourceUri = null;
            LoadAnimation();
        }
    }

    private Uri? _sourceUri;

    /// <summary>从 avares://（或相对资源）URI 加载动画。与 <see cref="JsonBytes"/> 二选一。</summary>
    public Uri? SourceUri
    {
        get => _sourceUri;
        set
        {
            _sourceUri = value;
            if (value is not null) ReadFromUri(value);
            else { _jsonBytes = null; LoadAnimation(); }
        }
    }

    private void ReadFromUri(Uri uri)
    {
        try
        {
            using var s = AssetLoader.Open(uri);
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            _jsonBytes = ms.ToArray();
            LoadAnimation();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[LottiePetControl] 读取资源失败 {uri}: {ex.Message}");
        }
    }

    /// <summary>播放速度倍率（1=原速）。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>是否循环（false=播到结尾停在末帧）。</summary>
    public bool Loop { get; set; } = true;

    public LottiePetControl()
    {
        ClipToBounds = false;
        _startMs = Environment.TickCount64;
    }

    private void LoadAnimation()
    {
        StopTimer();
        _animation?.Dispose();
        _animation = null;
        _elapsed = TimeSpan.Zero;
        _lastTick = null;

        if (_jsonBytes is not { Length: > 0 }) { InvalidateVisual(); return; }

        try
        {
            using var data = SKData.CreateCopy(_jsonBytes);
            _animation = Animation.Create(data);
            _aw = (int)_animation.Size.Width;
            _ah = (int)_animation.Size.Height;
            System.Diagnostics.Trace.WriteLine(
                $"[LottiePetControl] 加载成功 size={_aw}x{_ah} dur={_animation.Duration} fps={_animation.Fps}");
            StartTimer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[LottiePetControl] 加载失败: {ex.Message}");
            _animation = null;
        }
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
        if (_animation is null) return;
        var now = DateTime.UtcNow;
        if (_lastTick is { } last)
        {
            _elapsed += TimeSpan.FromSeconds((now - last).TotalSeconds * EffectiveSpeed);
            if (Loop && _animation.Duration > TimeSpan.Zero && _elapsed > _animation.Duration)
                _elapsed = TimeSpan.Zero;
            else if (!Loop && _elapsed >= _animation.Duration)
                _elapsed = _animation.Duration;
        }
        _lastTick = now;
        InvalidateVisual();
    }

    /// <summary>按 mood 的有效播放速度（= 用户 Speed × mood 倍率）。</summary>
    private double EffectiveSpeed => _mood switch
    {
        PetMood.Thinking => Speed * 0.55, // 深思慢放
        PetMood.Working   => Speed * 1.35, // 忙碌快进
        PetMood.Speaking  => Speed * 1.0,
        _                 => Speed,
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_animation is null || _aw <= 0 || _ah <= 0) return;

        int dw = (int)(Width > 0 ? Width : Bounds.Width);
        int dh = (int)(Height > 0 ? Height : Bounds.Height);
        if (dw <= 0 || dh <= 0) return;

        // 1. 离屏渲染 Lottie 当前帧到 display 尺寸的 SKBitmap（透明底，canvas.Scale 缩放动画）。
        EnsureFrameSurface(dw, dh);
        using (var canvas = new SKCanvas(_frameBitmap!))
        {
            canvas.Clear(SKColors.Transparent);
            var sx = (float)dw / _aw;
            var sy = (float)dh / _ah;
            canvas.Scale(sx, sy);

            // 语义状态动效：呼吸/脉冲缩放 + 上下浮，围绕中心按 mood 节奏变化。
            ApplyMoodMotion(canvas, dw, dh);

            _animation.SeekFrameTime(_elapsed, null);
            _animation.Render(canvas, new SKRect(0, 0, _aw, _ah));
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
    /// 切换语义状态（信号联动入口）。Waiting/Thinking/Working/Speaking 映射自聊天信号，
    /// 经 <see cref="PetWindow.ApplyProgress"/>/token 流调用。改 mood 只调节奏与脉冲，不重载动画。
    /// </summary>
    public void SetMood(PetMood mood)
    {
        if (_mood == mood)
        {
            if (mood != PetMood.Speaking) return; // Speaking 需持续刷新口型节奏，不跳过
        }
        _mood = mood;
        // 切换状态时重置节奏相位，避免脉冲/浮动从旧相位跳变。
        _startMs = Environment.TickCount64;
        InvalidateVisual();
    }

    /// <summary>当前语义状态。</summary>
    public PetMood Mood => _mood;

    /// <summary>停播并释放动画 + 定时器 + 位图（窗口关闭/换形象时调）。</summary>
    public void StopAndDispose()
    {
        StopTimer();
        _animation?.Dispose();
        _animation = null;
        _frameBitmap?.Dispose();
        _frameBitmap = null;
        _displayBitmap?.Dispose();
        _displayBitmap = null;
    }
}
