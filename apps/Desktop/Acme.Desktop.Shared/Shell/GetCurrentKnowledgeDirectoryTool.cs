using System.ComponentModel;
using System.IO;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>GetCurrentKnowledgeDirectory</c> 工具：让 agent 查资料库（labs/知识对象）目录的绝对路径与顶层结构。
/// 与 <see cref="GetCurrentProjectDirectoryTool"/> 平行——后者取文档页打开的项目目录，此工具取资料库目录。
/// 资料库目录由 <see cref="IKnowledgeDirectoryProvider"/> 持有（独立单例，与项目目录解耦）：
/// 用户在知识对象页打开过目录则用 <see cref="IKnowledgeDirectoryProvider.CurrentDirectory"/>，
/// 否则回退 <see cref="IKnowledgeDirectoryProvider.DefaultRoot"/>（宿主设为 <c>&lt;tenantDir&gt;/labs</c>，
/// 始终存在）。故此工具任何会话都能拿到资料库绝对路径——agent 据此配合 ReadFile/Grep/ExploreDirectory
/// 读写资料库文件（agents/rules/workflows/knowledge/skills/mcp-servers…），或作为 git 工具的 workingDirectory
/// 对资料库目录跑 git（项目目录与资料库目录是两个独立仓库，见 <see cref="GitTool"/>）。
/// </summary>
public sealed class GetCurrentKnowledgeDirectoryTool
{
    private readonly IKnowledgeDirectoryProvider _provider;

    /// <summary>顶层条目（目录优先排序，排除构建产物/VCS/依赖缓存，限 50 条）。</summary>
    public sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    public sealed class Output
    {
        /// <summary>资料库目录绝对路径。</summary>
        public string? KnowledgePath { get; set; }
        public string? KnowledgeName { get; set; }
        /// <summary>是否解析到有效目录（CurrentDirectory 或 DefaultRoot 任一存在）。</summary>
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

    public GetCurrentKnowledgeDirectoryTool(IKnowledgeDirectoryProvider provider) => _provider = provider;

    [Description("获取资料库（labs/知识对象）目录的绝对路径与顶层文件/目录结构。" +
                 "资料库是 agent/rule/workflow/knowledge/skill/mcp-server 等创作资产的存放处（默认 <tenantDir>/labs）。" +
                 "用户在知识对象页打开过目录则用该目录，否则回退默认 labs 根（始终存在）。" +
                 "拿到绝对路径后可用 ReadFile/Grep/Glob/ExploreDirectory 读写资料库文件，或作为 git_status 等 git 工具的 workingDirectory 参数对资料库跑 git。")]
    public async Task<Output> GetCurrentKnowledgeDirectory(CancellationToken cancellationToken = default)
    {
        var path = _provider.CurrentDirectory ?? _provider.DefaultRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new Output
            {
                IsOpen = false,
                Error = "未配置资料库目录（DefaultRoot 未设或路径无效）。",
            };
        }

        // 目录枚举移入 Task.Run：同步 Directory.EnumerateFileSystemEntries + 每项 Directory.Exists
        // 在调用线程跑；移出以彻底消除 UI 线程同步 IO 风险。
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
                    KnowledgePath = path,
                    KnowledgeName = name,
                    IsOpen = true,
                    Entries = entries,
                };
            }
            catch (Exception ex)
            {
                return new Output { KnowledgePath = path, IsOpen = true, Error = $"读取目录结构失败：{ex.Message}" };
            }
        }, cancellationToken);
    }
}
