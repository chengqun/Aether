using System;
using System.IO;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 文件树图标解析：按文件名/扩展名返回对应语言/类型图标（path data + 色 + 是否填充）。
/// 对齐 VSCode——文件夹不显图标（只靠展开箭头），文件按后缀显语言/类型图标。
/// 复用 <see cref="NavIcons"/> 的语言 logo path data（零重复美术资源）；
/// NavIcons 没有的语言/类型用内联 Lucide stroke 图标（项目/解决方案/配置/媒体/密钥/代码文件等）配品牌色。
/// 无匹配回退通用文件图标（描边灰）。UI-agnostic。
/// </summary>
public static class FileIcons
{
    public sealed record Icon(string Path, string Color, bool IsFill);

    /// <summary>通用文件图标（无匹配时回退）。描边灰，明暗两主题均可见。</summary>
    public static Icon Default { get; } = new(FileNode.FileIcon, "#7A8290", false);

    /// <summary>知识对象图标（<c>.md</c>/<c>.md</c> 知识对象文件——对标 note 节点 knowledge 图标：Lucide 知识 stroke + 青，非语言 logo）。</summary>
    public static Icon Knowledge { get; } = new(NavIcons.Knowledge, "#0891B2", false);

    /// <summary>技能图标（<c>SKILL.md</c>——Lucide sparkles stroke + 紫，非语言 logo；供 labs 树技能节点）。</summary>
    public static Icon Skill { get; } = new(NavIcons.Sparkles, "#7C3AED", false);

    // —— 内联 Lucide stroke 图标（IsFill=false）。品牌色配语义，明暗两主题均可见。 ——
    // 项目文件（.csproj 等）——立方体/包。
    private const string ProjectIcon =
        "M16.5 9.4 7.5 4.21M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z M3.27 6.96 12 12.01l8.73-5.05M12 22.08V12";
    // 解决方案（.sln/.slnx）——层叠。
    private const string SolutionIcon =
        "M12 2 2 7l10 5 10-5-10-5Z M2 17l10 5 10-5M2 12l10 5 10-5";
    // 代码文件（无品牌 logo 的语言回退此形 + 品牌色）——文件 + 尖括号。
    private const string FileCodeIcon =
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M9 13l2 2-2 2M13 13l-2 2 2 2";
    // 配置（.toml/.ini/.env 等）——滑杆。
    private const string ConfigIcon =
        "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6";
    // 锁（.lock/.pem/.key 等密钥/证书）。
    private const string LockIcon =
        "M5 11h14v10H5z M8 11V7a4 4 0 0 1 8 0v4";
    // 图片。
    private const string ImageIcon =
        "M3 3h18v18H3z M21 15l-5-5L5 21M9 9a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
    // 视频。
    private const string VideoIcon =
        "M23 7l-7 5 7 5V7zM1 5h15v14H1z";
    // 音频。
    private const string MusicIcon =
        "M9 18V5l12-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z";
    // 压缩包。
    private const string ArchiveIcon =
        "M21 8v13H3V8M1 3h22v5H1zM10 12h4";
    // Git（.gitignore 等）——分支。
    private const string GitIcon =
        "M6 3v12M18 9a3 3 0 1 0-6 0 3 3 0 0 0 6 0z M9 9a3 3 0 1 0-6 0 3 3 0 0 0 6 0z M9 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z";
    // 盾牌（LICENSE 等）。
    private const string ShieldIcon =
        "M12 2 4 5v6c0 5 3.5 8.5 8 10 4.5-1.5 8-5 8-10V5z";

    /// <summary>按文件名解析图标。先匹配特殊文件名（Dockerfile/.gitignore 等），再按扩展名。</summary>
    public static Icon ForFile(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var lower = name.ToLowerInvariant();

        // —— 特殊文件名（无意义扩展名 / 点开头配置 / 约定名）——
        switch (lower)
        {
            case "dockerfile":
                return new(ProjectIcon, "#2496ED", false); // Docker 蓝
            case "makefile" or "gnumakefile":
                return new(FileCodeIcon, "#4EAA25", false);
            case ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitconfig" or ".git-blame-ignore-revs":
                return new(GitIcon, "#F1502F", false);
            case "license" or "license.txt" or "license.md" or "copying":
                return new(ShieldIcon, "#6B7280", false);
        }
        if (lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal))
            return new(ConfigIcon, "#EAB308", false);
        if (lower == ".dockerignore")
            return new(ProjectIcon, "#2496ED", false);

        // —— 扩展名 ——
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) return Default;
        return ext.ToLowerInvariant() switch
        {
            // .NET：项目/解决方案与源码区分
            ".csproj" or ".vbproj" or ".fsproj" => new(ProjectIcon, "#8B5CF6", false),
            ".sln" or ".slnx" => new(SolutionIcon, "#6D28D9", false),
            ".cs" or ".vb" or ".fs" => new(NavIcons.LangDotnet, "#512BD4", true),
            // 脚本/标记（NavIcons 品牌 logo）
            ".py"   => new(NavIcons.LangPython, "#3776AB", true),
            ".js" or ".mjs" or ".cjs" => new(NavIcons.LangJavascript, "#D97706", true),
            ".ts"   => new(NavIcons.LangJavascript, "#3178C6", true), // TS 用 JS logo + 蓝（区别 JS 橙）
            ".json" => new(NavIcons.LangJson, "#6B6B70", true),
            ".md" or ".markdown" => new(NavIcons.LangMarkdown, "#6B6B70", true),
            ".html" or ".htm" => new(NavIcons.LangHtml5, "#E34F26", true),
            ".css"   => new(NavIcons.LangCss3, "#1572B6", true),
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".h" => new(NavIcons.LangCplusplus, "#00599C", true),
            ".java"  => new(NavIcons.LangOpenJdk, "#F89820", true), // Java 橙亮版（黑在深主题不可见）
            ".xml" or ".axaml" or ".xaml" => new(NavIcons.LangXml, "#005FAD", true),
            ".sql"   => new(NavIcons.LangSqlite, "#00A4C2", true), // 亮蓝
            ".ps1" or ".sh" or ".bash" or ".zsh" => new(NavIcons.LangBash, "#4EAA25", true),
            ".yaml" or ".yml" => new(NavIcons.LangYaml, "#CB171E", true),
            // 其余语言（无品牌 logo path）：代码文件形 + 品牌色
            ".go"   => new(FileCodeIcon, "#00ADD8", false),
            ".rs"   => new(FileCodeIcon, "#CE422B", false),
            ".jsx" or ".tsx" => new(FileCodeIcon, "#3178C6", false),
            ".vue"  => new(FileCodeIcon, "#41B883", false),
            ".svelte" => new(FileCodeIcon, "#FF3E00", false),
            ".php"  => new(FileCodeIcon, "#777BB4", false),
            ".rb"   => new(FileCodeIcon, "#CC342D", false),
            ".swift" => new(FileCodeIcon, "#F05138", false),
            ".kt" or ".kts" => new(FileCodeIcon, "#7F52FF", false),
            ".dart" => new(FileCodeIcon, "#0175C2", false),
            ".lua"  => new(FileCodeIcon, "#5271A4", false),
            ".r"    => new(FileCodeIcon, "#276DC3", false),
            ".scala" or ".sc" => new(FileCodeIcon, "#DC322F", false),
            ".ex" or ".exs" => new(FileCodeIcon, "#6E4A7E", false),
            ".hs"   => new(FileCodeIcon, "#5D4F85", false),
            ".clj" or ".cljs" or ".cljc" => new(FileCodeIcon, "#5881D8", false),
            ".erl"  => new(FileCodeIcon, "#A90533", false),
            ".ml" or ".mli" => new(FileCodeIcon, "#F1881A", false),
            ".pl" or ".pm" => new(FileCodeIcon, "#5B7FB9", false),
            ".groovy" or ".gradle" => new(FileCodeIcon, "#4298B8", false),
            ".proto" => new(FileCodeIcon, "#4285F4", false),
            ".graphql" or ".gql" => new(FileCodeIcon, "#E10098", false),
            // 配置
            ".toml" or ".ini" or ".cfg" or ".conf" or ".config" or ".properties" or ".editorconfig" => new(ConfigIcon, "#6B7280", false),
            ".lock" or ".lock.json" => new(LockIcon, "#EAB308", false),
            // 密钥/证书
            ".pem" or ".crt" or ".cer" or ".key" or ".pfx" or ".p12" => new(LockIcon, "#EAB308", false),
            // 媒体
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".webp" or ".bmp" or ".tiff" or ".tif" => new(ImageIcon, "#EC4899", false),
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".flv" or ".wmv" => new(VideoIcon, "#EF4444", false),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" => new(MusicIcon, "#A855F7", false),
            // 压缩包
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => new(ArchiveIcon, "#F59E0B", false),
            _ => Default,
        };
    }
}
