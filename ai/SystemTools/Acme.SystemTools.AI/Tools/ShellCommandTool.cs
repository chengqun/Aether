using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.SystemTools.AI.Tools;

/// <summary>
/// Shell 命令执行工具 — 支持管道(|)、重定向(>)、链式操作(&&)等 Shell 特性。
/// 与 CommandExecutorTool 的区别：本工具通过 Shell 进程执行命令，
/// 支持 Shell 内置功能和管道组合，适合代码编译、测试运行等复杂命令场景。
/// </summary>
public class ShellCommandTool
{
    [System.ComponentModel.Description("执行 Shell 命令，支持管道(|)、重定向(>)和链式操作(&&)")]
    public async Task<Output> ExecuteShellCommand(
        [System.ComponentModel.Description("Shell 命令字符串，如 'dotnet build 2>&1 | head -20'")] string command,
        [System.ComponentModel.Description("工作目录（可选）")] string? workingDirectory = null,
        [System.ComponentModel.Description("超时时间（毫秒），默认60000")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new Output { ExitCode = -1, StandardOutput = string.Empty, StandardError = "命令不能为空", Success = false };

        var actualTimeout = timeoutMs ?? 60000;

        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // 重定向 stdin 并立即关闭：防止命令读 stdin 永久阻塞
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };

            // 根据平台选择 Shell。用 ArgumentList 传参，避免命令串里的引号/空格被错切
            // （旧实现 Arguments = $"-c \"{command}\" 在命令含双引号时会截断/错乱）。
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }

            process = new Process { StartInfo = psi };
            process.Start();
            // 立即关闭 stdin：任何读 stdin 的子命令拿到 EOF 而非永久阻塞。
            try { process.StandardInput.Close(); } catch { }

            // 异步读取 stdout 和 stderr 避免死锁
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // 等待进程退出或超时
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(actualTimeout);

            bool cancelledByUser = false;
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时（非外部取消）：进程已在下方 finally 被 kill。
                var partialStdout = await stdoutTask;
                var partialStderr = await stderrTask;
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = Truncate(partialStdout, 50000),
                    StandardError = $"命令执行超时（{actualTimeout}ms），已终止进程。\n{Truncate(partialStderr, 10000)}",
                    Success = false,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户主动停止：进程已在下方 finally 被 kill。
                cancelledByUser = true;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (cancelledByUser)
            {
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = Truncate(stdout, 50000),
                    StandardError = $"命令执行已被取消（用户停止）。\n{Truncate(stderr, 10000)}",
                    Success = false,
                };
            }

            return new Output
            {
                ExitCode = process.ExitCode,
                StandardOutput = Truncate(stdout, 50000),
                StandardError = Truncate(stderr, 10000),
                Success = process.ExitCode == 0,
            };
        }
        catch (Exception ex)
        {
            return new Output
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = $"执行 Shell 命令失败: {ex.Message}",
                Success = false,
            };
        }
        finally
        {
            // 任何退出路径（正常/超时/取消/异常）都确保杀掉进程树，避免孤儿进程堆积吃 CPU/内存。
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { /* 进程可能已退出 */ }
                process.Dispose();
            }
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + $"\n... (已截断，总长度 {text.Length} 字符)";
    }

    public class Output
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
