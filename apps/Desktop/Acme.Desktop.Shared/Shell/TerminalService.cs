using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using RoyalTerminal.Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="ITerminalService"/> 实现：持会话元数据 + 哨兵捕获逻辑；控件/PTY 生命周期委托给已附着的 <see cref="TerminalView"/>。
/// 镜像 <see cref="AskUserDialogService"/>（接口在 Mvvm，impl 在 Shared）。单例——一个终端面板/窗口。
/// </summary>
/// <remarks>
/// 控件由 <see cref="TerminalView"/> 拥有（每会话一个 <see cref="TerminalControl"/> + 真实 PTY）。本服务经
/// <see cref="AttachView"/>/『DetachView』拿视图引用，转发 Create/Close/SendInput/RunAsync。RunAsync 的哨兵扫描
/// 在本服务内（统一一处）：向控件 SendInput 命令 + OSC 8331 退出码哨兵，订阅 <see cref="TerminalControl.DataReceived"/>
/// 扫原始字节命中哨兵拿退出码。Workbench 加载即建全部 panel 视图（IsVisible 切换不销毁）→ 视图常驻，PTY 跨 nav 不断。
/// </remarks>
public sealed class TerminalService : ITerminalService
{
    private readonly Dictionary<Guid, SessionInfo> _sessions = new();
    private readonly object _lock = new();
    private TerminalView? _view;
    private Guid? _activeId;

    public Guid? ActiveSessionId { get { lock (_lock) return _activeId; } }

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? SessionExited;

    // —— 视图附着（TerminalView.OnAttached 调）——
    public void AttachView(TerminalView view)
    {
        _view = view;
        // 视图附着前可能已建会话（理论上不会，因工作台加载即建视图），补建控件 + 激活。
        List<SessionInfo> snapshot;
        Guid? active;
        lock (_lock) { snapshot = _sessions.Values.ToList(); active = _activeId; }
        foreach (var s in snapshot)
            view.CreateSessionControl(s);
        if (active is { } aid)
            view.Activate(aid);
    }

    public void DetachView(TerminalView view)
    {
        if (ReferenceEquals(_view, view))
        {
            view.DisposeAllControls();
            _view = null;
        }
    }

    public ITerminalSession CreateSession(string? cwd, string? title)
    {
        var info = new SessionInfo(Guid.NewGuid(), cwd, title ?? DefaultTitle(), IsRunning: true);
        lock (_lock)
        {
            _sessions[info.Id] = info;
            _activeId = info.Id;
        }
        _view?.CreateSessionControl(info);
        _view?.Activate(info.Id);
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(info.Id);
        return info;
    }

    public void CloseSession(Guid sessionId)
    {
        _view?.RemoveControl(sessionId);
        bool removed;
        Guid? newActive = null;
        lock (_lock)
        {
            removed = _sessions.Remove(sessionId);
            if (removed && _activeId == sessionId)
            {
                _activeId = _sessions.Keys.LastOrDefault();
                newActive = _activeId;
            }
        }
        if (removed)
        {
            if (newActive is { } aid) { _view?.Activate(aid); ActiveSessionChanged?.Invoke(aid); }
            SessionsChanged?.Invoke();
        }
    }

    public void SetActive(Guid sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId)) return;
            _activeId = sessionId;
        }
        _view?.Activate(sessionId);
        ActiveSessionChanged?.Invoke(sessionId);
    }

    public IReadOnlyList<ITerminalSession> GetSessions()
    {
        lock (_lock) return _sessions.Values.ToList();
    }

    public void SendInput(Guid sessionId, string text) => _view?.SendInput(sessionId, text);

    public async Task<TerminalRunResult> RunAsync(Guid sessionId, string command, int timeoutMs, CancellationToken ct)
    {
        var view = _view;
        var ctrl = view?.GetControl(sessionId);
        if (ctrl is null || !ctrl.HasPty)
            return new TerminalRunResult { Error = "终端会话不存在或尚未启动 shell" };

        // 哨兵：OSC 8331 + 退出码。shell 收到的命令文本含 printf '\x1b]8331;%d\x07'（字面 backslash-x，
        // 非真实 ESC），printf 输出真实 ESC/BEL 字节——回显里无真实 0x1b，扫描不误命中。
        var fullCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{command}; Write-Host -NoNewline \"$([char]27)]8331;$LASTEXITCODE$([char]7)\""
            : $"{command}; printf '\\x1b]8331;%d\\x07' \"$?\"";

        var capture = new List<byte>();
        var captureLock = new object();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scanPos = 0;

        void OnData(object? s, TerminalDataEventArgs e)
        {
            int exitCode;
            lock (captureLock)
            {
                capture.AddRange(e.Data.Span.ToArray());
                if (TryFindSentinel(capture, ref scanPos, out exitCode))
                {
                    done.TrySetResult(exitCode);
                    return;
                }
            }
        }

        ctrl.DataReceived += OnData;
        try
        {
            ctrl.SendInput(fullCmd + "\n");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await done.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var partial = ReadCapture();
                if (ct.IsCancellationRequested)
                    return new TerminalRunResult { Completed = false, Output = partial,
                        Error = "命令执行已被取消（用户停止），进程仍在终端运行（可手动 Ctrl-C）" };
                return new TerminalRunResult { Completed = false, Output = partial,
                    Error = $"命令执行超时（{timeoutMs}ms），进程仍在终端运行（可在终端手动停止）" };
            }

            int? exit = await done.Task.ConfigureAwait(false);
            return new TerminalRunResult { ExitCode = exit, Completed = true, Output = ReadCapture() };
        }
        finally
        {
            ctrl.DataReceived -= OnData;
        }

        string ReadCapture()
        {
            byte[] bytes;
            lock (captureLock) bytes = capture.ToArray();
            // 截到哨兵前（哨兵字节之后无内容，但保守起见截到首个 ESC]8331;）
            var text = StripAnsi(Encoding.UTF8.GetString(bytes));
            // 丢弃首行（shell 回显的命令行——单行命令的回显占一行；多行命令的回显可能多行，但首行截断已大幅降噪）。
            var nl = text.IndexOf('\n');
            if (nl >= 0 && nl < text.Length) text = text[(nl + 1)..];
            return text.Trim();
        }
    }

    // —— 视图回呼：控件运行时事件 → 更新元数据 + 通知 VM ——
    internal void NotifyTitleChanged(Guid id, string title)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
                s = s with { Title = title };
            else return;
            _sessions[id] = s;
        }
        SessionsChanged?.Invoke();
    }

    internal void NotifyExited(Guid id)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
                _sessions[id] = s with { IsRunning = false };
            else return;
        }
        SessionsChanged?.Invoke();
        SessionExited?.Invoke(id);
    }

    private static string DefaultTitle() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "shell";

    /// <summary>在 capture 的 scanPos.. 之间扫描哨兵 ESC ] 8331 ; &lt;digits&gt; BEL。命中返回退出码并推进 scanPos 过哨兵。</summary>
    private static bool TryFindSentinel(List<byte> buf, ref int scanPos, out int exitCode)
    {
        exitCode = 0;
        // 哨兵前缀：ESC(0x1B) ']' '8' '3' '3' '1' ';'
        for (int i = scanPos; i < buf.Count - 1; i++)
        {
            if (buf[i] != 0x1B || buf[i + 1] != (byte)']') continue;
            // 匹配 ]8331;
            if (i + 6 >= buf.Count) break; // 不够长，等更多字节
            if (buf[i + 2] != (byte)'8' || buf[i + 3] != (byte)'3' || buf[i + 4] != (byte)'3' ||
                buf[i + 5] != (byte)'1' || buf[i + 6] != (byte)';')
            {
                // 别的 OSC（如标题），跳过这个 ESC——但仍可能其后跟 8331，保守只进 1
                continue;
            }
            // 读数字直到 BEL(0x07)
            int j = i + 7;
            var digits = new StringBuilder();
            while (j < buf.Count && buf[j] != 0x07 && buf[j] != 0x1B && j - (i + 7) < 12)
            {
                if (buf[j] >= (byte)'0' && buf[j] <= (byte)'9') digits.Append((char)buf[j]);
                else break;
                j++;
            }
            if (j >= buf.Count || buf[j] != 0x07)
            {
                // 哨兵未完整（BEL 未到）或异常中断；保留 scanPos 在 i 之前，等更多字节
                scanPos = i;
                return false;
            }
            if (digits.Length > 0 && int.TryParse(digits.ToString(), out exitCode))
            {
                scanPos = j + 1;
                return true;
            }
            // 数字解析失败，跳过
        }
        // 保留尾部一小段（哨兵前缀最长 7 字节 + 数字 12 + BEL）防漏
        scanPos = Math.Max(0, buf.Count - 20);
        return false;
    }

    /// <summary>剥离 ANSI/OSC/CSI 转义 + CR，保留换行。OSC 8331 哨兵亦被剥（输出不含哨兵）。</summary>
    private static readonly Regex AnsiStrip = new(
        @"\][^]*(|\\)|\[[0-9;?]*[ -/]*[@-~]|[@-Z\\-_]|\r",
        RegexOptions.Compiled);

    private static string StripAnsi(string s) => AnsiStrip.Replace(s, string.Empty);

    private sealed record SessionInfo(Guid Id, string? Cwd, string Title, bool IsRunning) : ITerminalSession;
}
