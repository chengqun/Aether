using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Build;
using Acme.SystemTools.AI.Tools;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>get_build_errors</c> / <c>run_tests</c> 工具：让 agent 拿到**结构化**编译错误与测试失败，
/// 而非 <c>ExecuteShellCommand</c> 跑 dotnet 后的裸输出（弱模型从上千行输出里找错容易漏）。
/// </summary>
/// <remarks>
/// 与 <see cref="GitTool"/> 同属宿主层注册的 AI 工具——需项目目录（
/// <see cref="ICurrentProjectDirectoryProvider"/>，文档页回写），故不走 ai/ 适配器。
/// 命令执行复用 <see cref="ShellCommandTool"/>（登录 shell <c>-l</c> 解决 .app 受限 PATH + 进程管理）。
/// 标 <c>Safe</c>：agentic 编程闭环核心高频动作，低摩擦（用户决策）。
/// </remarks>
public sealed class BuildTool
{
    private readonly ICurrentProjectDirectoryProvider _projectDir;
    private readonly ShellCommandTool _shell;

    public BuildTool(ICurrentProjectDirectoryProvider projectDir, ShellCommandTool shell)
    {
        _projectDir = projectDir;
        _shell = shell;
    }

    // —— get_build_errors ——

    public sealed class BuildErrorItem
    {
        public string File { get; set; } = string.Empty;
        public int Line { get; set; }
        public int Column { get; set; }
        public string Severity { get; set; } = string.Empty;   // "error" | "warning"
        public string Code { get; set; } = string.Empty;       // 如 "CS0246"；无码为空
        public string Message { get; set; } = string.Empty;
    }

    public sealed class BuildOutput
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<BuildErrorItem> Errors { get; set; } = new();
        public string? ProjectPath { get; set; }
        /// <summary>stdout 尾部约 30 行——兜底非 MSBuild-error 格式的问题（NuGet restore / SDK 版本等）。</summary>
        public string? StdoutTail { get; set; }
        public string? Error { get; set; }
    }

    [Description("编译当前项目（文档页打开的目录）并返回结构化编译错误/警告列表。改完代码后用此工具确认能否编译通过——比 ExecuteCommand 跑 dotnet build 更准：直接拿到 file/line/错误码/消息，不用从上千行输出里找。自动跑，不审批。")]
    public async Task<BuildOutput> GetBuildErrors(
        [Description("要编译的项目/solution 路径（可选，默认自动找当前目录下 .slnx/.sln/.csproj）")] string? projectPath = null,
        [Description("编译配置，默认 Debug")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir))
            return new BuildOutput { Error = "当前未打开项目目录（请在文档页打开一个项目目录）。" };

        var project = ResolveProject(projectPath, dir);
        if (project is null)
            return new BuildOutput { Error = $"在 {dir} 及其一级子目录下未找到 .slnx/.sln/.csproj。请传 projectPath 指定。" };

        var config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
        // --verbosity minimal：只 error/warning + summary，避免输出超 50000 截断丢错误。2>&1 合并 stderr。
        var command = $"dotnet build \"{project}\" -c {config} --nologo --verbosity minimal 2>&1";
        var r = await _shell.ExecuteShellCommand(command, dir, 180000, cancellationToken).ConfigureAwait(false);

        var errors = MsBuildOutputParser.Parse(r.StandardOutput);
        var errorCount = errors.Count(e => e.Severity == "error");
        return new BuildOutput
        {
            Success = r.Success,
            ExitCode = r.ExitCode,
            ErrorCount = errorCount,
            WarningCount = errors.Count(e => e.Severity == "warning"),
            Errors = errors.Select(e => new BuildErrorItem
            {
                File = e.File, Line = e.Line, Column = e.Column,
                Severity = e.Severity, Code = e.Code, Message = e.Message,
            }).ToList(),
            ProjectPath = project,
            StdoutTail = Tail(r.StandardOutput, 30),
            // 编译失败但没解析到标准错误行——多半是 NuGet/SDK/项目文件问题，提示看 tail。
            Error = r.Success ? null
                : errorCount > 0 ? null
                : "编译失败但未解析到标准 MSBuild 错误行，请看 StdoutTail 诊断（可能是 NuGet restore / SDK 版本 / 项目文件问题）。",
        };
    }

    // —— run_tests ——

    public sealed class TestFailureItem
    {
        public string Name { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
    }

    public sealed class TestOutput
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public List<TestFailureItem> Failures { get; set; } = new();
        public string? ProjectPath { get; set; }
        public string? Error { get; set; }
    }

    [Description("运行当前项目（文档页打开的目录）的单元测试并返回结构化结果（通过/失败数 + 失败用例的错误消息与堆栈）。比 ExecuteCommand 跑 dotnet test 更准：直接拿到失败用例名/断言失败/堆栈，不用从输出里找。会先编译（不传 --no-build）。自动跑，不审批。")]
    public async Task<TestOutput> RunTests(
        [Description("要测试的项目/solution 路径（可选，默认自动找）")] string? projectPath = null,
        [Description("测试过滤器，如 'FullyQualifiedName~Acme.Quotes'（可选）")] string? filter = null,
        [Description("编译配置，默认 Debug")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir))
            return new TestOutput { Error = "当前未打开项目目录（请在文档页打开一个项目目录）。" };

        var project = ResolveProject(projectPath, dir);
        if (project is null)
            return new TestOutput { Error = $"在 {dir} 及其一级子目录下未找到 .slnx/.sln/.csproj。请传 projectPath 指定。" };

        var config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
        var filterArg = string.IsNullOrWhiteSpace(filter) ? string.Empty : $" --filter \"{filter.Trim()}\"";
        var command = $"dotnet test \"{project}\" -c {config} --nologo{filterArg} 2>&1";
        var r = await _shell.ExecuteShellCommand(command, dir, 300000, cancellationToken).ConfigureAwait(false);

        var summary = TestOutputParser.Parse(r.StandardOutput);
        return new TestOutput
        {
            Success = summary.Success,
            ExitCode = r.ExitCode,
            Total = summary.Total,
            Passed = summary.Passed,
            Failed = summary.Failed,
            Skipped = summary.Skipped,
            Failures = summary.Failures.Select(f => new TestFailureItem
            {
                Name = f.Name, ErrorMessage = f.ErrorMessage, StackTrace = f.StackTrace,
            }).ToList(),
            ProjectPath = project,
            Error = r.Success ? null : (summary.Failed > 0 ? null : "测试运行失败但未解析到摘要行，请看 dotnet 原始输出诊断。"),
        };
    }

    // —— helpers ——

    /// <summary>定位要 build/test 的项目文件：传参 > 顶层 .slnx > .sln > .csproj > 一级子目录同序。</summary>
    private static string? ResolveProject(string? projectPath, string dir)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            try { var full = Path.GetFullPath(projectPath); return File.Exists(full) ? full : null; }
            catch { return null; }
        }
        return FindByExtension(dir, [".slnx", ".sln", ".csproj"]);
    }

    private static string? FindByExtension(string dir, string[] extensions)
    {
        foreach (var ext in extensions)
        {
            var hit = Directory.GetFiles(dir, "*" + ext).FirstOrDefault();
            if (hit is not null) return hit;
        }
        // 一级子目录
        foreach (var sub in Directory.GetDirectories(dir))
        {
            foreach (var ext in extensions)
            {
                var hit = Directory.GetFiles(sub, "*" + ext).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static string? Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var all = text.Split('\n');
        var take = all.Length <= lines ? all : all.Skip(all.Length - lines);
        return string.Join('\n', take.Select(s => s.TrimEnd('\r'))).Trim();
    }
}
