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
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档版知识对象（<c>.md</c> 文件）AI 工具，提供 4 工具
/// （List/Read/Create/Update），操作对象为磁盘 <c>.md</c> 文件。操作磁盘文件需知「知识库目录」——
/// <see cref="IKnowledgeDirectoryProvider"/> 在
/// apps 层（<c>Acme.Desktop.Mvvm</c>），<c>ai/</c> 适配器不可引 apps/，故此工具住宿主层（同
/// <see cref="GetCurrentProjectDirectoryTool"/>/<c>BuildTool</c>/<c>RunInTerminalTool</c>），在 <c>AddDesktopCommon</c>
/// 内联注册。解析/序列化复用 <see cref="IDocumentKnowledgeSourceParser"/>（<c>AddNotesInfrastructure</c> 已注册 DI 单例）。
/// 知识对象文件路径解析（Read 的 link 目标展开 + 图闭包）经 <see cref="OkTargetResolver"/> 共享，与文档编辑器图解析同源。
/// <para><b>作用域</b>：仅操作 labs 的 <c>knowledge/</c> 子树（<c>NoteNodeType.Knowledge</c> 对应目录）——
/// List 只枚举 <c>knowledge/</c>、Create 始终落 <c>knowledge/</c>、Read/Update 越界（指向 <c>agents/</c>、
/// <c>rules/</c> 等非知识对象 .md）则拒绝。防 AI 把 agent <c>.agent.md</c> / 各目录 README 误当知识对象读写。
/// 自定义知识库目录（无 <c>knowledge/</c> 子目录）回退为整目录操作（向后兼容）。link 解析仍根植 labs 根，
/// 与知识对象页表单编辑器（<c>DocumentKnowledgeViewModel</c>）同源，两者 link 路径写法一致。</para>
/// </summary>
public sealed class DocumentKnowledgeTool
{
    private readonly IKnowledgeDirectoryProvider _knowledgeDir;
    private readonly IDocumentKnowledgeSourceParser _parser;

    public DocumentKnowledgeTool(IKnowledgeDirectoryProvider knowledgeDir, IDocumentKnowledgeSourceParser parser)
    {
        _knowledgeDir = knowledgeDir ?? throw new ArgumentNullException(nameof(knowledgeDir));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    // —— DTO（镜像 note KnowledgeTool，文档版增 Path 字段——权威引用是相对路径，非 Guid） ——

    public sealed class DocumentKnowledgeSummary
    {
        /// <summary>相对路径（不含 .md，如 业务分析/退款会员日志分析）——用于 Read/Create/Update 的 name 入参。</summary>
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
        /// <summary>组装后的完整知识（kind + 正文 + 各 link 指向的子 .md 文件原文）。</summary>
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

    [Description("列出文档版知识对象（.md 文件，磁盘上的知识资产）。返回路径/名称/kind/正文预览。" +
                 "仅枚举 labs 知识对象目录（knowledge/ 子树）；自定义知识库目录（无 knowledge/ 子目录）则枚举整个目录。" +
                 "可选 folder 只列指定子目录（知识对象目录内的相对路径，如 业务分析；可带 knowledge/ 前缀）；不传列整个知识对象目录。需先在知识对象页打开知识库目录。")]
    public async Task<ListDocumentKnowledgeOutput> ListDocumentKnowledgeObjects(
        [Description("知识对象目录内的子目录相对路径（可选；如 业务分析 或 knowledge/业务分析）")] string? folder = null,
        CancellationToken cancellationToken = default)
    {
        var root = _knowledgeDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new ListDocumentKnowledgeOutput { Error = "当前未打开知识库目录。请在知识对象页打开知识库目录后再用文档知识对象工具。" };

        var (knowledgeRoot, isLabs) = ResolveScope(root);
        string baseDir;
        if (string.IsNullOrWhiteSpace(folder))
            baseDir = knowledgeRoot;
        else
        {
            // labs 模式下剥 knowledge/ 前缀（让 folder 带不带前缀等价）；自定义目录原样拼接。
            var folderNorm = isLabs ? StripKnowledgePrefix(folder) : folder;
            baseDir = Path.Combine(knowledgeRoot, folderNorm);
        }
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

    [Description("按相对路径读取一个文档知识对象（.md 文件）的完整定义：kind + 正文 + links 指向的子 .md 文件内容（只展开一层）。" +
                 "仅可读取 labs knowledge/ 子树内的知识对象（路径越界指向 agents/ 等 → 拒绝，防误读非知识对象 .md）。" +
                 "name=相对路径（不含 .md；如 knowledge/业务分析/退款会员日志分析 或 业务分析/退款会员日志分析，两种写法均可解析）。先调此工具获取知识再执行任务。" +
                 "link 解析支持三种 targetId 写法：知识库根相对（权威）/ 相对源文件目录（./b、../xx/b）/ 裸名（全局 basename 匹配）。")]
    public async Task<ReadDocumentKnowledgeOutput> ReadDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 knowledge/业务分析/退款 或 业务分析/退款；也可带 .md 后缀）")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ReadDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new ReadDocumentKnowledgeOutput { Error = "当前未打开知识库目录。请在知识对象页打开知识库目录后再用文档知识对象工具。" };

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (knowledgeRoot, isLabs) = ResolveScope(root);
                // 候选：知识库根（labs）相对 + 知识对象目录（knowledge/）相对。后者让裸路径（业务分析/退款）也能解析。
                var candidates = OkTargetResolver.CandidateOkPaths(name, root, root).ToList();
                if (isLabs)
                    candidates.AddRange(OkTargetResolver.CandidateOkPaths(StripKnowledgePrefix(name), knowledgeRoot, knowledgeRoot));
                // 越界守卫：只接受 knowledge/ 子树内的文件（防误读 agent/规则/README 等非知识对象 .md）。
                var full = candidates.FirstOrDefault(p => File.Exists(p) && IsUnder(p, knowledgeRoot));
                if (full is null)
                    return new ReadDocumentKnowledgeOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };

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
                    // link 解析仍根植 labs（与文档编辑器图解析同源，保持 page↔tool 一致）。
                    Definition = Format(def, relPath, root),
                };
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ReadDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("创建一个文档知识对象（.md 文件）。name=相对路径（不含扩展名）；labs 模式下始终创建在 knowledge/ 子树内" +
                 "（自动补 knowledge/ 前缀，name 带不带 knowledge/ 均可）。kind 分类自由字符串（流程/数据源/规则/Entity 等）。" +
                 "links 可选 JSON 数组：" +
                 "{\"role\":\"uses\",\"targetId\":\"Hive表/fin_dw\"}（targetId 三种写法：知识库根相对=权威；" +
                 "相对源文件目录 ./b 或 ../xx/b；裸名 b 全局匹配——推荐目录内互链用裸名/相对路径，目录迁移不断链）。" +
                 "已存在则失败。")]
    public async Task<CreateDocumentKnowledgeOutput> CreateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md 扩展名；如 业务分析/测试KO 或 knowledge/业务分析/测试KO）")] string name,
        [Description("分类，自由字符串：流程 / 数据源 / 规则 / Entity 等")] string kind,
        [Description("markdown 正文：人类说明、字段表、SQL 模板、示例输出等")] string body,
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"targetId\":\"Hive表/fin_dw\"}]")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new CreateDocumentKnowledgeOutput { Error = "当前未打开知识库目录。请在知识对象页打开知识库目录后再用文档知识对象工具。" };

        try
        {
            var (knowledgeRoot, _) = ResolveScope(root);
            // labs 模式：始终落 knowledge/ 子树（name 带 knowledge/ 前缀先剥再补，幂等）。
            var nameNoExt = StripKnowledgePrefix(StripOkExt(name));
            if (string.IsNullOrWhiteSpace(nameNoExt))
                return new CreateDocumentKnowledgeOutput { Error = "名称不能为空" };
            var full = Path.Combine(knowledgeRoot, nameNoExt + ".md");
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
            var relPath = ToRelativePath(root, full);
            return new CreateDocumentKnowledgeOutput { Success = true, Path = relPath, Name = OkTargetResolver.StemOf(nameNoExt) };
        }
        catch (Exception ex)
        {
            return new CreateDocumentKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("更新一个文档知识对象（.md 文件）的 kind/body/links。按相对路径定位（仅可更新 labs knowledge/ 子树内的知识对象，" +
                 "路径越界拒绝）；传哪个参数更新哪个，不传保留原值。links 全量替换。name=相对路径（不含 .md）。")]
    public async Task<UpdateDocumentKnowledgeOutput> UpdateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 knowledge/业务分析/退款 或 业务分析/退款）")] string name,
        [Description("新 kind（可选，不传保留原值）")] string? kind = null,
        [Description("新 markdown 正文（可选，不传保留原值）")] string? body = null,
        [Description("新 links（可选，JSON 数组，全量替换；不传保留原 links）。targetId=相对路径权威 或 target=名称")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new UpdateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new UpdateDocumentKnowledgeOutput { Error = "当前未打开知识库目录。请在知识对象页打开知识库目录后再用文档知识对象工具。" };

        try
        {
            var (knowledgeRoot, isLabs) = ResolveScope(root);
            var candidates = OkTargetResolver.CandidateOkPaths(name, root, root).ToList();
            if (isLabs)
                candidates.AddRange(OkTargetResolver.CandidateOkPaths(StripKnowledgePrefix(name), knowledgeRoot, knowledgeRoot));
            // 越界守卫：只接受 knowledge/ 子树内的文件（防误改 agent/规则/README 等非知识对象 .md）。
            var full = candidates.FirstOrDefault(p => File.Exists(p) && IsUnder(p, knowledgeRoot));
            if (full is null)
                return new UpdateDocumentKnowledgeOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };

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

    /// <summary>组装：path + kind + name + 正文 + 各 link 指向子 .md 文件的原文（含 target 路径，2 格缩进，只展开一层）。
    /// 镜像 note KnowledgeTool.FormatAsync——文档版"目标 Content"= 目标 .md 文件全文（经 <see cref="OkTargetResolver.ResolveRaw"/>）。
    /// <para><b>防上下文溢出</b>：link 可能指向含完整字段表/SQL/示例的大 .md，多目标内联会撑爆 LLM 上下文窗口
    /// （曾触发 GLM <c>ResponseEnded</c> 提前终止 → "The response ended prematurely"）。故每目标内联有上限、累计有上限，
    /// 超出截断头部 + 标记总数并告知 agent 如何取完整内容——同 <c>GrepTool</c>/<c>run_in_terminal</c> 的 Truncated 范式。</para></summary>
    private const int MaxTargetInlineChars = 8000;       // 单个目标 .md 原文内联上限
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
            // link 解析语境：currentFileDir=被读文件自身目录（与图视图打开 .md 时传 Path.GetDirectoryName(filePath) 同构）。
            // 这使裸名（b）/同目录（./b）/上层目录（../xx/b）写法在 Read 展开时也能解析，与图口径统一；
            // 根相对（毓数平台项目/xxx）仍是第一优先级，兼容既有 42 条根相对 links。
            var currentFileDir = Path.GetDirectoryName(Path.Combine(root, relPath));
            foreach (var l in def.Links)
            {
                var resolved = OkTargetResolver.ResolveRaw(l.Target, root, currentFileDir);
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
                    sb.AppendLine("  （找不到知识对象文件）");
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

    /// <summary>递归枚举知识对象文件（<c>.md</c>；跳过构建产物/VCS/依赖目录 + 符号链接目录，上限 200）。</summary>
    private void EnumerateOkFiles(string dir, string root, List<DocumentKnowledgeSummary> list, CancellationToken ct)
    {
        if (list.Count >= 200) return;
        string[]? subdirs = null;
        string[]? files = null;
        try
        {
            files = Directory.GetFiles(dir, "*.md");
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

    /// <summary>去掉末尾的 <c>.md</c> 扩展名（知识对象相对路径规范=无扩展名）。</summary>
    private static string StripOkExt(string path)
        => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && path.Length > 3
            ? path.Substring(0, path.Length - 3)
            : path;

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

    // —— 知识对象目录作用域助手（labs knowledge/ 子树约束） ——

    /// <summary>labs 中知识对象子目录名（<c>knowledge/</c>），取自 <see cref="CatalogNoteDirectory"/>（单一来源）。</summary>
    private static readonly string KnowledgeSubdirName =
        CatalogNoteDirectory.For(NoteNodeType.Knowledge).DirectoryName; // "knowledge"

    /// <summary>解析工具的作用域：labs 模式（根含 <c>knowledge/</c> 子目录）→ 返回该子目录 + isLabs=true；
    /// 自定义知识库目录（无 <c>knowledge/</c> 子目录）→ 返回根本身 + isLabs=false（整目录即知识对象库，向后兼容）。</summary>
    private static (string KnowledgeRoot, bool IsLabs) ResolveScope(string root)
    {
        var knowledgeSubdir = Path.Combine(root, KnowledgeSubdirName);
        if (Directory.Exists(knowledgeSubdir))
            return (Path.GetFullPath(knowledgeSubdir), true);
        return (Path.GetFullPath(root), false);
    }

    /// <summary>剥去路径开头的 <c>knowledge/</c> 段（labs 模式下让裸路径 <c>业务分析/退款</c> 与带前缀
    /// <c>knowledge/业务分析/退款</c> 等价，两种写法解析到同一文件）。无 <c>knowledge/</c> 前缀原样返回。</summary>
    private static string StripKnowledgePrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var norm = path.Replace('\\', '/');
        if (norm.Length > KnowledgeSubdirName.Length + 1
            && string.Equals(norm[..KnowledgeSubdirName.Length], KnowledgeSubdirName, StringComparison.OrdinalIgnoreCase)
            && norm[KnowledgeSubdirName.Length] == '/')
            return path[(KnowledgeSubdirName.Length + 1)..];
        return path;
    }

    /// <summary>路径是否在 <paramref name="baseDir"/> 子树内（越界守卫：防误读/误改 <c>agents/</c>、<c>rules/</c>
    /// 等非知识对象 .md）。比较规范化全路径（忽略大小写 + 目录分隔符）。</summary>
    private static bool IsUnder(string full, string baseDir)
    {
        try
        {
            var fullNorm = Path.GetFullPath(full);
            var baseNorm = Path.GetFullPath(baseDir);
            return string.Equals(fullNorm, baseNorm, StringComparison.OrdinalIgnoreCase)
                   || fullNorm.StartsWith(baseNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
