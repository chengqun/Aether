using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>一个终端会话的 UI 无关句柄（标识 + 元数据）。真实 PTY/控件生命周期由视图层 impl 代管。</summary>
public interface ITerminalSession
{
    Guid Id { get; }
    string? Cwd { get; }
    string Title { get; }
    /// <summary>shell 进程是否仍在运行（退出后变 false，标签保留供用户关闭）。</summary>
    bool IsRunning { get; }
}

/// <summary><see cref="ITerminalService.RunAsync"/> 的返回：单条命令在可见终端执行的结局。</summary>
public sealed class TerminalRunResult
{
    /// <summary>命令退出码（哨兵解析得到）；未完成时为 null。</summary>
    public int? ExitCode { get; set; }
    /// <summary>命令是否在超时/取消前完成（哨兵命中）。false = 进程仍在终端运行（可观察/可手动停）。</summary>
    public bool Completed { get; set; }
    /// <summary>捕获的终端输出（已剥离 ANSI/OSC 噪音，但含命令回显——需干净输出用 ExecuteShellCommand）。</summary>
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>
/// 集成终端程序化 API（接口在 Mvvm，UI 无关，镜像 <see cref="IAskUserDialogService"/>；impl 在 Shared 持 RoyalTerminal 控件）。
/// 用户面板（<see cref="TerminalViewModel"/>）与 agent 工具（RunInTerminalTool）共用：前者管会话生命周期，
/// 后者让 agent 把命令发到**可见**终端（真实 PTY/完整 env/可中断）而非静默管道。
/// </summary>
/// <remarks>
/// 视图层 impl 持有 <c>RoyalTerminal.Avalonia.Controls.TerminalControl</c> 实例（每会话一个），起真实登录 shell。
/// Workbench 在工作台加载时一次性创建所有 panel 视图（<see cref="Acme.Desktop.Workbench.Workspace.WorkspaceView"/> 的 BuildTabbed），
/// 靠 <c>IsVisible</c> 切换而非销毁 → 终端视图跨 nav 切换常驻（仅隐藏），PTY 进程不被打断。
/// </remarks>
public interface ITerminalService
{
    /// <summary>新建终端会话（起真实登录 shell，cwd 默认项目目录）。返回会话句柄。</summary>
    ITerminalSession CreateSession(string? cwd, string? title);
    /// <summary>关闭会话：杀 PTY 子进程树 + 释放控件 + 移除标签。</summary>
    void CloseSession(Guid sessionId);
    /// <summary>设活跃会话（标签切换 → 视图层切显隐）。</summary>
    void SetActive(Guid sessionId);
    /// <summary>当前所有会话元数据快照（供 VM 投影标签列表）。</summary>
    IReadOnlyList<ITerminalSession> GetSessions();
    /// <summary>当前活跃会话 id（无会话为 null）。</summary>
    Guid? ActiveSessionId { get; }
    /// <summary>向会话 PTY 发送原始文本（键入到 shell），不等待。</summary>
    void SendInput(Guid sessionId, string text);
    /// <summary>在可见终端跑一条命令：发送命令 + 哨兵回显（OSC 8331 + 退出码），等待完成/超时/取消，
    /// 返回退出码 + 捕获输出。命令在终端可见、可手动 Ctrl-C。超时/取消时进程继续在终端跑。</summary>
    Task<TerminalRunResult> RunAsync(Guid sessionId, string command, int timeoutMs, CancellationToken ct);

    /// <summary>会话列表变更（新建/关闭/运行态变化）——视图层触发，VM 刷新标签。</summary>
    event Action? SessionsChanged;
    /// <summary>活跃会话变更（标签切换）——视图层触发，VM 同步选中态。</summary>
    event Action<Guid>? ActiveSessionChanged;
    /// <summary>会话 shell 退出——视图层触发，VM 标记标签为已停止。</summary>
    event Action<Guid>? SessionExited;

    /// <summary>底部终端面板是否可见（VSCode 式底部面板方案 B：可折叠）。折叠时仅隐藏 region 空间，PTY 不中断。</summary>
    bool IsPanelVisible { get; }
    /// <summary>底部终端面板显隐变更。WorkspaceView 订阅以折叠/展开 region 的 Grid 行高；shell VM 订阅以同步 toggle 按钮态。</summary>
    event Action<bool>? PanelVisibilityChanged;
    /// <summary>设置底部终端面板可见性并持久化到 preferences（ui.terminalVisible）。终端折叠按钮 / 侧边栏 toggle 调此。</summary>
    void SetPanelVisible(bool visible);
}

/// <summary>终端标签元数据（VM 绑定用；由 <see cref="ITerminalService.GetSessions"/> 投影）。</summary>
public partial class TerminalSessionItem : ObservableObject
{
    public Guid Id { get; }
    public string? Cwd { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isActive;

    public TerminalSessionItem(Guid id, string? cwd, string title, bool isRunning = true)
    {
        Id = id;
        Cwd = cwd;
        _title = title;
        _isRunning = isRunning;
    }
}
