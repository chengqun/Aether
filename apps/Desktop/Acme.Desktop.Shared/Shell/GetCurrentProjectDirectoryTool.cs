using System.ComponentModel;
using System.IO;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>GetCurrentProjectDirectory</c> 工具：让 agent 查文档页当前打开的项目目录路径与顶层结构。
/// 桌面宿主在 .app 启动时 CWD=/，SystemTools 的 Glob/Grep/Shell 等默认 path 工具打不到用户打开的项目；
/// 此工具经 <see cref="ICurrentProjectDirectoryProvider"/>（DocumentsViewModel 回写）取真实目录，
/// 返回路径 + 顶层条目，模型据此配合 ReadFile/Grep/ExploreDirectory 读写项目文件。
/// </summary>
public sealed class GetCurrentProjectDirectoryTool
{
    private readonly ICurrentProjectDirectoryProvider _provider;

    /// <summary>顶层条目（目录优先排序，排除构建产物/VCS/依赖缓存，限 50 条）。</summary>
    public sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    public sealed class Output
    {
        /// <summary>当前打开的项目目录绝对路径（null = 未打开）。</summary>
        public string? ProjectPath { get; set; }
        public string? ProjectName { get; set; }
        /// <summary>是否已打开项目目录。</summary>
        public bool IsOpen { get; set; }
        /// <summary>顶层文件/目录条目（仅 IsOpen 时填充）。</summary>
        public List<Entry> Entries { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>目录树扫描时跳过的子项（构建产物 / VCS / 依赖缓存，体积大且无阅读价值）。</summary>
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".next", "dist", "build", "target",
        ".dart_tool", ".gradle", "__pycache__", ".venv", "venv", "publish"
    };

    public GetCurrentProjectDirectoryTool(ICurrentProjectDirectoryProvider provider) => _provider = provider;

    [Description("获取文档页当前打开的项目目录路径与顶层文件/目录结构。" +
                 "用户在「文档」页打开项目目录后，此工具返回其绝对路径与顶层条目（目录优先，排除 bin/obj/.git/node_modules 等）。" +
                 "未打开目录时返回 IsOpen=false。拿到路径后可用 ReadFile/Grep/Glob/ExploreDirectory 等工具读写该项目。")]
    public async Task<Output> GetCurrentProjectDirectory(CancellationToken cancellationToken = default)
    {
        var path = _provider.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new Output
            {
                IsOpen = false,
                Error = "当前未打开项目目录。请先在「文档」页用「文件 ▾ 打开项目目录」打开一个目录。",
            };
        }

        // 目录枚举（虽限 50 项）移入 Task.Run：同步 Directory.EnumerateFileSystemEntries + 每项 Directory.Exists
        // 在调用线程跑；MAF 工具分派线程通常在 ThreadPool，但仍移出以彻底消除 UI 线程同步 IO 风险。
        return await Task.Run(() =>
        {
            try
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = path;

                var entries = new List<Entry>();
                foreach (var full in Directory.EnumerateFileSystemEntries(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = Path.GetFileName(full);
                    if (ExcludedNames.Contains(entryName)) continue;
                    var isDir = Directory.Exists(full);
                    entries.Add(new Entry { Name = entryName, IsDirectory = isDir });
                    if (entries.Count >= 50) break;
                }
                // 目录优先，各自按名排序。
                entries = entries
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new Output
                {
                    ProjectPath = path,
                    ProjectName = name,
                    IsOpen = true,
                    Entries = entries,
                };
            }
            catch (Exception ex)
            {
                return new Output { ProjectPath = path, IsOpen = true, Error = $"读取目录结构失败：{ex.Message}" };
            }
        }, cancellationToken);
    }
}
