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
            _elapsed += TimeSpan.FromSeconds((now - last).TotalSeconds * Speed);
            if (Loop && _animation.Duration > TimeSpan.Zero && _elapsed > _animation.Duration)
                _elapsed = TimeSpan.Zero;
            else if (!Loop && _elapsed >= _animation.Duration)
                _elapsed = _animation.Duration;
        }
        _lastTick = now;
        InvalidateVisual();
    }

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
