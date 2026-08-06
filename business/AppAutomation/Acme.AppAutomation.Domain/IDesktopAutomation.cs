namespace Acme.AppAutomation.Domain;

/// <summary>
/// 桌面控制原语端口（OS 无关 / 机制无关）。固定过程（<c>Procedure</c>）在代码里调这些原语来驱动 GUI。
/// 实现：<c>McpDesktopAutomation</c>（经 MCP server 调 FlaUI/AX 工具）。测试用录制 mock。
/// <para>这是「固定过程路径」的 C# 原语抽象；「LLM 直连路径」不经此接口——agent 直接调 MCP server 工具。</para>
/// </summary>
public interface IDesktopAutomation
{
    /// <summary>桌面控制后端是否就绪（MCP server 已连接）。</summary>
    bool IsConnected { get; }

    Task FocusWindowAsync(string titleOrId, CancellationToken ct = default);
    Task<bool> WindowExistsAsync(string titleOrId, CancellationToken ct = default);

    Task ClickAsync(DesktopLocator locator, CancellationToken ct = default);
    Task DoubleClickAsync(DesktopLocator locator, CancellationToken ct = default);
    Task RightClickAsync(DesktopLocator locator, CancellationToken ct = default);

    Task TypeAsync(string text, CancellationToken ct = default);
    /// <summary>按键组合，如 "ctrl+a" / "command+a" / "enter"。</summary>
    Task KeyAsync(string combo, CancellationToken ct = default);

    Task<DesktopElement?> FindElementAsync(DesktopLocator locator, CancellationToken ct = default);
    Task<string?> GetTextAsync(DesktopLocator locator, CancellationToken ct = default);
    /// <summary>取 accessibility 树（桌面 DOM）。scope 可选（窗口/控件 id）。</summary>
    Task<string> GetUITreeAsync(string? scope = null, CancellationToken ct = default);

    Task<bool> WaitForElementAsync(DesktopLocator locator, int timeoutMs = 5000, CancellationToken ct = default);
    Task SleepAsync(int ms, CancellationToken ct = default);
}
