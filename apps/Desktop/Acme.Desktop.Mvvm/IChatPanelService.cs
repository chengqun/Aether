using System;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// AI 聊天面板显隐程序化 API（接口在 Mvvm，UI 无关，镜像 <see cref="ITerminalService"/> 的显隐三件套；impl 在 Shared）。
/// 侧边栏「AI 聊天」toggle 按钮 + 聊天面板内 ✕ 折叠按钮共用此服务：开关聊天右栏（VSCode 式可折叠侧栏，对齐 ChatGPT 桌面端布局）。
/// </summary>
/// <remarks>
/// 折叠时仅隐藏 region 空间（列宽 0 + content IsVisible=false），聊天视图**不销毁**——
/// Workbench 加载即建全部 panel 视图（<c>IsVisible</c> 切换不销毁）→ 聊天跨 nav 切换常驻（仅隐藏），
/// 流式回复不中断（同终端 PTY / 浏览器 Playwright 会话的 view-owns-controls 模式）。
/// </remarks>
public interface IChatPanelService
{
    /// <summary>右侧 AI 聊天面板是否可见。折叠时仅隐藏 region 空间，视图常驻、流式不中断。</summary>
    bool IsChatVisible { get; }

    /// <summary>右侧 AI 聊天面板显隐变更。WorkspaceView 订阅以折叠/展开 region 的 Grid 列宽；shell VM 订阅以同步 toggle 按钮态。</summary>
    event Action<bool>? ChatVisibilityChanged;

    /// <summary>设置右侧 AI 聊天面板可见性并持久化到 preferences（ui.chatVisible）。侧栏 toggle / 聊天面板内 ✕ 调此。</summary>
    void SetChatVisible(bool visible);
}
