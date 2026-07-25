using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.SystemTools.AI;

public class CommandExecutorTool
{
    public class Output
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool Success => ExitCode == 0;
        public string? Error { get; set; }
    }

    [Description("执行命令行命令")]
    public async Task<Output> ExecuteCommand([Description("要执行的命令")] string command, [Description("命令参数")] string? arguments = null, [Description("工作目录")] string? workingDirectory = null, [Description("超时时间（毫秒）")] int? timeoutMs = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(command))
            {
                return new Output { Error = "命令不能为空" };
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using var process = new Process { StartInfo = processStartInfo };
            using var outputWaitHandle = new ManualResetEvent(false);
            using var errorWaitHandle = new ManualResetEvent(false);

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                {
                    outputWaitHandle.Set();
                }
                else
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                {
                    errorWaitHandle.Set();
                }
                else
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = timeoutMs ?? 30000;
            var completed = await Task.Run(() => process.WaitForExit(timeout), cancellationToken);

            if (!completed)
            {
                try { process.Kill(); } catch { }
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = outputBuilder.ToString(),
                    StandardError = errorBuilder.ToString(),
                    Error = $"命令执行超时（{timeout}ms）"
                };
            }

            outputWaitHandle.WaitOne();
            errorWaitHandle.WaitOne();

            return new Output
            {
                ExitCode = process.ExitCode,
                StandardOutput = outputBuilder.ToString(),
                StandardError = errorBuilder.ToString()
            };
        }
        catch (Exception ex)
        {
            return new Output
            {
                ExitCode = -1,
                Error = $"执行命令失败: {ex.Message}"
            };
        }
    }
}
