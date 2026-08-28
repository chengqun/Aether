using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="ILeftTreePanelService"/> 实现：仅持主侧栏（左栏）手动 toggle 态 + 持久化
/// （无控件/树生命周期——树视图由 Workbench 常驻、IsVisible 切换）。镜像 <see cref="ChatPanelService"/> 的显隐三件套（接口在 Mvvm，impl 在 Shared）。单例。
/// </summary>
/// <remarks>
/// 左栏有效可见由 WorkspaceView 叠加 tab 判定（手动 toggle && 编辑器类 tab），本服务只存 toggle 态。
/// 折叠时仅隐藏 region 空间（列宽 0 + content IsVisible=false），树视图不销毁、展开状态不丢。
/// </remarks>
public sealed class LeftTreePanelService : ILeftTreePanelService
{
    private readonly IPreferencesStore? _prefs;
    private bool _isVisible = true;
    private readonly object _lock = new();

    /// <summary>主侧栏（左栏）手动 toggle 持久化键（preferences.json）。</summary>
    private const string LeftTreeVisiblePrefKey = "ui.leftTreeVisible";

    public LeftTreePanelService(IPreferencesStore? prefs = null)
    {
        _prefs = prefs;
        // JsonPreferencesStore.GetAsync 同步完成（Task.FromResult），构造期 .GetAwaiter().GetResult() 安全不死锁。
        try
        {
            var v = prefs?.GetAsync(LeftTreeVisiblePrefKey).GetAwaiter().GetResult();
            _isVisible = !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase); // 默认可见，仅 "false" 折叠
        }
        catch { _isVisible = true; }
    }

    /// <inheritdoc/>
    public bool IsLeftTreeVisible { get { lock (_lock) return _isVisible; } }

    /// <inheritdoc/>
    public event Action<bool>? LeftTreeVisibilityChanged;

    /// <inheritdoc/>
    public void SetLeftTreeVisible(bool visible)
    {
        lock (_lock) _isVisible = visible;
        LeftTreeVisibilityChanged?.Invoke(visible);
        // SetAsync 真异步，fire-and-forget 持久化（SafeFireAndForget 记 Trace 不静默吞异常）。
        if (_prefs is not null)
            Task.Run(async () => await _prefs.SetAsync(LeftTreeVisiblePrefKey, visible ? "true" : "false")).SafeFireAndForget();
    }
}
