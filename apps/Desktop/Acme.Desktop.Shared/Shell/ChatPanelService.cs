using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IChatPanelService"/> 实现：仅持聊天右栏显隐态 + 持久化（无控件/会话生命周期——聊天视图由 Workbench 常驻、IsVisible 切换）。
/// 镜像 <see cref="TerminalService"/> 的显隐三件套（接口在 Mvvm，impl 在 Shared）。单例——一个聊天面板/窗口。
/// </summary>
/// <remarks>
/// 聊天视图不销毁：Workbench 加载即建全部 panel 视图（IsVisible 切换不销毁）→ 聊天跨 nav 切换常驻（仅隐藏），
/// 流式回复不中断（同终端 PTY / 浏览器 Playwright 会话的 view-owns-controls 模式）。折叠时仅隐藏 region 空间（列宽 0 + content IsVisible=false）。
/// </remarks>
public sealed class ChatPanelService : IChatPanelService
{
    private readonly IPreferencesStore? _prefs;
    private bool _isChatVisible = true;
    private readonly object _lock = new();

    /// <summary>右侧 AI 聊天面板显隐持久化键（preferences.json）。</summary>
    private const string ChatVisiblePrefKey = "ui.chatVisible";

    public ChatPanelService(IPreferencesStore? prefs = null)
    {
        _prefs = prefs;
        // JsonPreferencesStore.GetAsync 同步完成（Task.FromResult），构造期 .GetAwaiter().GetResult() 安全不死锁。
        try
        {
            var v = prefs?.GetAsync(ChatVisiblePrefKey).GetAwaiter().GetResult();
            _isChatVisible = !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase); // 默认可见，仅 "false" 折叠
        }
        catch { _isChatVisible = true; }
    }

    /// <inheritdoc/>
    public bool IsChatVisible { get { lock (_lock) return _isChatVisible; } }

    /// <inheritdoc/>
    public event Action<bool>? ChatVisibilityChanged;

    /// <inheritdoc/>
    public void SetChatVisible(bool visible)
    {
        lock (_lock) _isChatVisible = visible;
        ChatVisibilityChanged?.Invoke(visible);
        // SetAsync 真异步，fire-and-forget 持久化（SafeFireAndForget 记 Trace 不静默吞异常）。
        if (_prefs is not null)
            Task.Run(async () => await _prefs.SetAsync(ChatVisiblePrefKey, visible ? "true" : "false")).SafeFireAndForget();
    }
}
