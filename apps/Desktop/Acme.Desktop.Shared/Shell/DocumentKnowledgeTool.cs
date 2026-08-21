using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档版知识对象（<c>.ok</c> 文件）AI 工具，镜像 note 版 <see cref="Acme.Notes.AI.Tools.KnowledgeTool"/> 的 4 工具
/// （List/Read/Create/Update），操作对象从 note 节点换成磁盘 <c>.ok</c> 文件。note 版注入 <c>INoteService</c>
/// （Application 契约）；文档版操作磁盘文件需知「项目目录」—— <see cref="ICurrentProjectDirectoryProvider"/> 在
/// apps 层（<c>Acme.Desktop.Mvvm</c>），<c>ai/</c> 适配器不可引 apps/，故此工具住宿主层（同
/// <see cref="GetCurrentProjectDirectoryTool"/>/<c>BuildTool</c>/<c>RunInTerminalTool</c>），在 <c>AddDesktopCommon</c>
/// 内联注册。解析/序列化复用 <see cref="IDocumentKnowledgeSourceParser"/>（<c>AddNotesInfrastructure</c> 已注册 DI 单例）。
/// .ok 文件路径解析（Read 的 link 目标展开 + 图闭包）经 <see cref="OkTargetResolver"/> 共享，与文档编辑器图解析同源。
/// </summary>
public sealed class DocumentKnowledgeTool
{
    private readonly ICurrentProjectDirectoryProvider _projectDir;
    private readonly IDocumentKnowledgeSourceParser _parser;

    public DocumentKnowledgeTool(ICurrentProjectDirectoryProvider projectDir, IDocumentKnowledgeSourceParser parser)
    {
        _projectDir = projectDir ?? throw new ArgumentNullException(nameof(projectDir));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    // —— DTO（镜像 note KnowledgeTool，文档版增 Path 字段——权威引用是相对路径，非 Guid） ——

    public sealed class DocumentKnowledgeSummary
    {
        /// <summary>相对路径（不含 .ok，如 业务分析/退款会员日志分析）——用于 Read/Create/Update 的 name 入参。</summary>
        public string? Path { get; set; }
        /// <summary>文件名 stem（显示名，如 退款会员日志分析）。</summary>
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? BodyPreview { get; set; }
    }

    public sealed class ListDocumentKnowledgeOutput
    {
        public List<DocumentKnowledgeSummary> KnowledgeObjects { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class ReadDocumentKnowledgeOutput
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        /// <summary>组装后的完整知识（kind + 正文 + 各 link 指向的子 .ok 文件原文）。</summary>
        public string? Definition { get; set; }
        public string? Error { get; set; }
    }

    public sealed class CreateDocumentKnowledgeOutput
    {
        public bool Success { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    public sealed class UpdateDocumentKnowledgeOutput
    {
        public bool Success { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    // —— 工具方法 ——

    [Description("列出文档版知识对象（.ok 文件，磁盘上的知识资产）。返回路径/名称/kind/正文预览。" +
                 "可选 folder 只列指定子目录（相对路径，如 业务分析）；不传列整个项目目录。需先在文档页打开项目目录。")]
    public async Task<ListDocumentKnowledgeOutput> ListDocumentKnowledgeObjects(
        [Description("子目录相对路径（可选，只列该子目录下；不传列整个项目目录）")] string? folder = null,
        CancellationToken cancellationToken = default)
    {
        var root = _projectDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new ListDocumentKnowledgeOutput { Error = "当前未打开项目目录。请在文档页打开项目目录后再用文档知识对象工具。" };

        var baseDir = string.IsNullOrWhiteSpace(folder) ? root : Path.Combine(root, folder);
        if (!Directory.Exists(baseDir))
            return new ListDocumentKnowledgeOutput { Error = $"目录不存在: {folder}" };

        try
        {
            var result = await Task.Run(() =>
            {
                var list = new List<DocumentKnowledgeSummary>();
                EnumerateOkFiles(baseDir, root, list, cancellationToken);
                return list;
            }, cancellationToken);
            return new ListDocumentKnowledgeOutput { KnowledgeObjects = result };
        }
        catch (Exception ex)
        {
            return new ListDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("按相对路径读取一个文档知识对象（.ok 文件）的完整定义：kind + 正文 + links 指向的子 .ok 文件内容（只展开一层）。" +
                 "name=相对路径（不含 .ok，如 业务分析/退款会员日志分析）。先调此工具获取知识再执行任务。")]
    public async Task<ReadDocumentKnowledgeOutput> ReadDocumentKnowledgeObject(
        [Description("相对路径（不含 .ok，如 业务分析/退款会员日志分析）；也可带 .ok 后缀")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ReadDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _projectDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new ReadDocumentKnowledgeOutput { Error = "当前未打开项目目录。请在文档页打开项目目录后再用文档知识对象工具。" };

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 解析目标 .ok 文件（项目根候选；无 currentFileDir 语境——文档工具操作全项目）。
                var full = OkTargetResolver.CandidateOkPaths(name, root, null)
                    .FirstOrDefault(p => File.Exists(p));
                if (full is null)
                    return new ReadDocumentKnowledgeOutput { Error = $"找不到 .ok 文件: {name}" };

                string raw;
                try { raw = File.ReadAllText(full); }
                catch (Exception ex) { return new ReadDocumentKnowledgeOutput { Error = $"读取失败: {ex.Message}" }; }

                if (!_parser.TryParse(raw, out var def, out var err) || def is null)
                    return new ReadDocumentKnowledgeOutput { Error = $"解析失败: {err}" };

                var relPath = ToRelativePath(root, full);
                return new ReadDocumentKnowledgeOutput
                {
                    Path = relPath,
                    Name = OkTargetResolver.StemOf(name),
                    Kind = def.Kind,
                    Definition = Format(def, relPath, root),
                };
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ReadDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("创建一个文档知识对象（.ok 文件）。name=相对路径（不含扩展名），将创建 <项目目录>/<name>.ok。" +
                 "kind 分类自由字符串（流程/数据源/规则/Entity 等）。links 可选 JSON 数组：" +
                 "[{\"role\":\"uses\",\"targetId\":\"Hive表/fin_dw\"}]（targetId=相对路径，权威）或 [{\"role\":\"uses\",\"target\":\"fin_dw\"}]。" +
                 "已存在则失败。")]
    public async Task<CreateDocumentKnowledgeOutput> CreateDocumentKnowledgeObject(
        [Description("相对路径（不含 .ok 扩展名，如 业务分析/测试KO）")] string name,
        [Description("分类，自由字符串：流程 / 数据源 / 规则 / Entity 等")] string kind,
        [Description("markdown 正文：人类说明、字段表、SQL 模板、示例输出等")] string body,
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"targetId\":\"Hive表/fin_dw\"}]")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _projectDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new CreateDocumentKnowledgeOutput { Error = "当前未打开项目目录。请在文档页打开项目目录后再用文档知识对象工具。" };

        try
        {
            var nameNoExt = StripOkExt(name);
            var full = Path.Combine(root, nameNoExt + ".ok");
            if (File.Exists(full))
                return new CreateDocumentKnowledgeOutput { Error = $"已存在: {nameNoExt}" };

            var links = ParseLinks(linksJson);
            var def = new KnowledgeObjectDefinition
            {
                Kind = kind ?? string.Empty,
                Body = body ?? string.Empty,
                Links = links,
            };
            var source = _parser.Serialize(def);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, source, cancellationToken);
            return new CreateDocumentKnowledgeOutput { Success = true, Path = nameNoExt, Name = OkTargetResolver.StemOf(nameNoExt) };
        }
        catch (Exception ex)
        {
            return new CreateDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("更新一个文档知识对象（.ok 文件）的 kind/body/links。按相对路径定位；传哪个参数更新哪个，不传保留原值。" +
                 "links 全量替换。name=相对路径（不含 .ok）。")]
    public async Task<UpdateDocumentKnowledgeOutput> UpdateDocumentKnowledgeObject(
        [Description("相对路径（不含 .ok，如 业务分析/退款会员日志分析）")] string name,
        [Description("新 kind（可选，不传保留原值）")] string? kind = null,
        [Description("新 markdown 正文（可选，不传保留原值）")] string? body = null,
        [Description("新 links（可选，JSON 数组，全量替换；不传保留原 links）。targetId=相对路径权威 或 target=名称")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new UpdateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _projectDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new UpdateDocumentKnowledgeOutput { Error = "当前未打开项目目录。请在文档页打开项目目录后再用文档知识对象工具。" };

        try
        {
            var full = OkTargetResolver.CandidateOkPaths(name, root, null)
                .FirstOrDefault(p => File.Exists(p));
            if (full is null)
                return new UpdateDocumentKnowledgeOutput { Error = $"找不到 .ok 文件: {name}" };

            var raw = await File.ReadAllTextAsync(full, cancellationToken);
            if (!_parser.TryParse(raw, out var def, out var err) || def is null)
                return new UpdateDocumentKnowledgeOutput { Error = $"解析失败: {err}" };

            // 按提供的参数部分更新（不传的保留原值）
            if (kind is not null) def.Kind = kind;
            if (body is not null) def.Body = body;
            if (linksJson is not null) def.Links = ParseLinks(linksJson);

            var source = _parser.Serialize(def);
            await File.WriteAllTextAsync(full, source, cancellationToken);

            var nameNoExt = StripOkExt(ToRelativePath(root, full));
            return new UpdateDocumentKnowledgeOutput { Success = true, Path = nameNoExt, Name = OkTargetResolver.StemOf(nameNoExt) };
        }
        catch (Exception ex)
        {
            return new UpdateDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    // —— 内部助手 ——

    /// <summary>解析 links JSON。文档版 <c>targetId</c>=相对路径（无 notes DB 反查）。
    /// <c>targetId</c> 优先 → <see cref="KnowledgeLink.Target"/>=路径；回退 <c>target</c>（名称）。两者都无跳过。
    /// 解析失败返空列表（容错，镜像 note）。<see cref="KnowledgeLink.TargetId"/> 留 null——
    /// 与 <see cref="DocumentKnowledgeSourceParser.ToDomainLink"/> 同构（序列化把 Target 写回 targetId 字段）。</summary>
    private static List<KnowledgeLink> ParseLinks(string? linksJson)
    {
        var result = new List<KnowledgeLink>();
        if (string.IsNullOrWhiteSpace(linksJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(linksJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var role = el.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                // targetId=相对路径（权威，文档版权威引用），回退 target（名称）。存入 Target，TargetId 留 null。
                string target = "";
                if (el.TryGetProperty("targetId", out var tidEl))
                    target = tidEl.GetString() ?? "";
                else if (el.TryGetProperty("target", out var tEl))
                    target = tEl.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(target)) continue;
                result.Add(new KnowledgeLink { Role = role, Target = target, TargetId = null });
            }
        }
        catch { /* 容错：links JSON 非法 → 返回空 links，仍创建/更新知识对象 */ }
        return result;
    }

    /// <summary>组装：path + kind + name + 正文 + 各 link 指向子 .ok 文件的原文（含 target 路径，2 格缩进，只展开一层）。
    /// 镜像 note KnowledgeTool.FormatAsync——文档版"目标 Content"= 目标 .ok 文件全文（经 <see cref="OkTargetResolver.ResolveRaw"/>）。
    /// <para><b>防上下文溢出</b>：link 可能指向含完整字段表/SQL/示例的大 .ok，多目标内联会撑爆 LLM 上下文窗口
    /// （曾触发 GLM <c>ResponseEnded</c> 提前终止 → "The response ended prematurely"）。故每目标内联有上限、累计有上限，
    /// 超出截断头部 + 标记总数并告知 agent 如何取完整内容——同 <c>GrepTool</c>/<c>run_in_terminal</c> 的 Truncated 范式。</para></summary>
    private const int MaxTargetInlineChars = 8000;       // 单个目标 .ok 原文内联上限
    private const int MaxLinkedInlineTotalChars = 20000; // 所有 link 原文内联累计上限（防多 link 累积撑爆）

    private string Format(KnowledgeObjectDefinition def, string relPath, string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"path: {relPath}");
        sb.AppendLine($"kind: {def.Kind}");
        sb.AppendLine($"name: {OkTargetResolver.StemOf(relPath)}");
        if (!string.IsNullOrWhiteSpace(def.Body))
            sb.AppendLine().AppendLine(def.Body);

        if (def.Links.Count > 0)
        {
            sb.AppendLine().AppendLine("links:");
            var linkedTotal = 0; // 已内联的目标原文累计字符
            foreach (var l in def.Links)
            {
                var resolved = OkTargetResolver.ResolveRaw(l.Target, root, null);
                if (resolved is null) continue; // 空白路径，跳过
                var (title, raw) = resolved.Value;
                sb.AppendLine($"[{l.Role}] {title} (path:{l.Target}):");

                if (linkedTotal >= MaxLinkedInlineTotalChars)
                {
                    // 累计已超上限：不再内联后续 link 原文，仅列名 + 提示（防多 link 累积撑爆）。
                    sb.AppendLine("  （引用过多，已停止展开后续目标；完整内容请单独 ReadDocumentKnowledgeObject 对应路径）");
                    continue;
                }

                if (raw is null)
                {
                    sb.AppendLine("  （找不到 .ok 文件）");
                }
                else if (raw.Length > MaxTargetInlineChars)
                {
                    // 单个目标过大：截断头部（字段表/schema 信息密度高从头开始）+ 标记总数。
                    var truncated = raw[..MaxTargetInlineChars]
                        + $"\n…（已截断，原文共 {raw.Length} 字符；如需完整内容请单独 ReadDocumentKnowledgeObject \"{l.Target}\"）";
                    sb.AppendLine(Indent(truncated, "  "));
                    linkedTotal += MaxTargetInlineChars;
                }
                else
                {
                    sb.AppendLine(Indent(raw, "  "));
                    linkedTotal += raw.Length;
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>递归枚举 <c>.ok</c> 文件（跳过构建产物/VCS/依赖目录 + 符号链接目录，上限 200）。</summary>
    private void EnumerateOkFiles(string dir, string root, List<DocumentKnowledgeSummary> list, CancellationToken ct)
    {
        if (list.Count >= 200) return;
        string[]? subdirs = null;
        string[]? files = null;
        try
        {
            files = Directory.GetFiles(dir, "*.ok");
            subdirs = Directory.GetDirectories(dir);
        }
        catch { /* 无权限/不存在 → 跳过该目录 */ }

        if (files is not null)
        {
            foreach (var f in files)
            {
                if (list.Count >= 200) return;
                ct.ThrowIfCancellationRequested();
                KnowledgeObjectDefinition? def = null;
                try { _ = _parser.TryParse(File.ReadAllText(f), out def, out _); }
                catch { /* 读/解析失败 → 仍列出（Kind/Preview 留空），让 agent 知道文件在 */ }
                var rel = ToRelativePath(root, f);
                list.Add(new DocumentKnowledgeSummary
                {
                    Path = rel,
                    Name = OkTargetResolver.StemOf(rel),
                    Kind = def?.Kind,
                    BodyPreview = string.IsNullOrWhiteSpace(def?.Body) ? null : Truncate(def!.Body, 80),
                });
            }
        }

        if (subdirs is not null)
        {
            foreach (var d in subdirs)
            {
                if (list.Count >= 200) return;
                var name = Path.GetFileName(d);
                if (IsIgnoredDir(name) || IsSymlinkDir(d)) continue;
                EnumerateOkFiles(d, root, list, ct);
            }
        }
    }

    private static string ToRelativePath(string root, string full)
    {
        try { return Path.GetRelativePath(root, full); }
        catch { return full; }
    }

    private static string StripOkExt(string path)
        => path.EndsWith(".ok", StringComparison.OrdinalIgnoreCase) && path.Length > 3
            ? path.Substring(0, path.Length - 3) : path;

    private static readonly HashSet<string> _ignoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", "dist", "build", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    private static bool IsIgnoredDir(string dirName)
        => dirName.StartsWith(".") || _ignoredDirs.Contains(dirName);

    private static bool IsSymlinkDir(string fullPath)
    {
        try { return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Indent(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return prefix;
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        return string.Join('\n', lines.Select(l => prefix + l));
    }
}
