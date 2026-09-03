using System.Xml.Linq;

namespace Aether.Architecture.Tests;

/// <summary>单个 csproj 的层化视角：名称、层、上下文、层级、Project/Package 引用（引用存 repo 相对路径，'/' 分隔）。</summary>
public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    string Layer,        // src | business | ai | apps | server | tests | mcp
    string? Context,     // business 上下文名（路径第二段，如 "Ctp"/"Trading.Runtime"）；其余层为 null
    string Tier,         // Domain | Application | Infrastructure | Other（按项目名后缀判定）
    IReadOnlyList<string> ProjectRefs,
    IReadOnlyList<string> PackageRefs);

/// <summary>
/// 全仓一方 csproj 依赖模型。从 <c>Aether.AI.Platform.slnx</c> 所在目录递归扫描（跳过 bin/obj/externals/隐藏目录），
/// 解析每个 csproj 的 ProjectReference / PackageReference。<c>externals/</c> 下是第三方引擎（Lean/MAF/CTP），不加载其
/// csproj、不做规则断言——指向 externals 的引用以 "externals/" 前缀路径原样保留，由 externals 白名单规则单独管辖。
/// 不解析 csproj 的 Import（Directory.Build.props / server/common.props）——当前无跨文件共享引用的需求。
/// </summary>
public sealed class RepoModel
{
    private static readonly string[] SkipDirNames = { "bin", "obj", "externals", "node_modules", "artifacts", "publish" };

    public required string Root { get; init; }
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public required IReadOnlyDictionary<string, ProjectInfo> ByPath { get; init; }

    public static RepoModel Load()
    {
        var root = FindRepoRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Where(rel => !IsSkipped(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .Select(rel => ParseProject(Path.Combine(root, rel), root))
            .ToList();
        return new RepoModel
        {
            Root = root,
            Projects = projects,
            ByPath = projects.ToDictionary(p => p.RelativePath, StringComparer.Ordinal),
        };
    }

    /// <summary>向上查找仓库根（含 Aether.AI.Platform.slnx 的最近祖先目录）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aether.AI.Platform.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"未找到仓库根（{AppContext.BaseDirectory} 起无 Aether.AI.Platform.slnx 祖先目录）");
    }

    /// <summary>跳过构建产物/第三方/隐藏目录——隐藏目录排除 .git/.claude/worktrees 等整仓克隆副本。</summary>
    private static bool IsSkipped(string relativePath)
    {
        var segs = relativePath.Split('/');
        return segs.Any(s => s.StartsWith('.')) || segs.Any(s => SkipDirNames.Contains(s, StringComparer.Ordinal));
    }

    private static ProjectInfo ParseProject(string csprojPath, string root)
    {
        var rel = Path.GetRelativePath(root, csprojPath).Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var segs = rel.Split('/');
        var layer = segs[0];
        var tier = name.EndsWith(".Domain", StringComparison.Ordinal) ? "Domain"
                 : name.EndsWith(".Application", StringComparison.Ordinal) ? "Application"
                 : name.EndsWith(".Infrastructure", StringComparison.Ordinal) ? "Infrastructure"
                 : "Other";

        var doc = XDocument.Load(csprojPath);
        var refs = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => NormalizeRef(csprojPath, v!, root))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var pkgs = doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ProjectInfo(name, rel, layer, layer == "business" ? segs[1] : null, tier, refs, pkgs);
    }

    /// <summary>把 csproj 内的相对引用（Windows 反斜杠风格兼容）解析为 repo 相对路径。</summary>
    private static string NormalizeRef(string csprojPath, string include, string root)
    {
        var full = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(csprojPath)!,
            include.Replace('\\', Path.DirectorySeparatorChar)));
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    public ProjectInfo? Resolve(string refPath) => ByPath.GetValueOrDefault(refPath);
}
