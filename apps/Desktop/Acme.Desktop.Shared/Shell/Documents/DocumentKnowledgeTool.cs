using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;
using Acme.Notes.Infrastructure.Knowledge;

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
                 "可选 folder 只列指定子目录（知识对象目录内的相对路径，如 业务分析；可带 knowledge/ 前缀）；不传列整个知识对象目录。" +
                 "资料库目录取知识对象页打开的目录，未打开则回退默认 labs 根。")]
    public async Task<ListDocumentKnowledgeOutput> ListDocumentKnowledgeObjects(
        [Description("知识对象目录内的子目录相对路径（可选；如 业务分析 或 knowledge/业务分析）")] string? folder = null,
        CancellationToken cancellationToken = default)
    {
        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new ListDocumentKnowledgeOutput { Error = "未配置资料库目录。" };

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

    [Description("按相对路径读取一个文档知识对象（.md 文件）的完整定义：kind + 正文 + links 指向的子 .md 文件内容。" +
                 "仅可读取 labs knowledge/ 子树内的知识对象（路径越界指向 agents/ 等 → 拒绝，防误读非知识对象 .md）。" +
                 "name=相对路径（不含 .md；如 knowledge/业务分析/退款会员日志分析 或 业务分析/退款会员日志分析，两种写法均可解析）。先调此工具获取知识再执行任务。" +
                 "\n展开深度（expandDepth，可选 int？）：null（默认）= 作者标志模式——只内联 link 上标了 expand:true 的目标，沿标过的链递归展开多层；" +
                 "未标 expand 的子节点只列在 links 清单（agent 按需单独 read 取内容）。这契合「当前页本身才重要」——既有知识对象无 expand 字段时默认只返回本对象。" +
                 "传 N（0~4）= 覆盖模式——忽略作者标志，blanket 内联 N 层全部子节点（N=0 仅本对象；N=1 等价于旧行为全部直接子节点内联）。" +
                 "硬上限 4 层 + 单目标 8000 字 / 累计 20000 字截断（超出提示单独 read 对应路径）。" +
                 "\nlink 解析支持三种 targetId 写法：知识库根相对（权威）/ 相对源文件目录（./b、../xx/b）/ 裸名（全局 basename 匹配）。")]
    public async Task<ReadDocumentKnowledgeOutput> ReadDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 knowledge/业务分析/退款 或 业务分析/退款；也可带 .md 后缀）")] string name,
        [Description("展开深度（可选）：null=作者标志模式（只内联标了 expand 的边）；N=覆盖模式 blanket 内联 N 层全部子节点（0~4）")] int? expandDepth = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ReadDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new ReadDocumentKnowledgeOutput { Error = "未配置资料库目录。" };

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
                    Definition = Format(def, relPath, root, full, expandDepth),
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
                 "每条 link 可选 expand:true 标记「AI 读取此对象时内联该目标内容」（默认 false=只列引用，agent 按需单独 read）。" +
                 "extraJson 可选 JSON 对象：写 front-matter 额外字段（kind/links 之外），如回写 Intelligence 时填 " +
                 "{\"subject\":\"…\",\"claim\":\"…\",\"source\":\"monitoring\",\"confidence\":\"high\",\"first_seen\":\"2026-09-20\",\"last_updated\":\"2026-09-20\",\"status\":\"active\"}。" +
                 "已存在则失败。")]
    public async Task<CreateDocumentKnowledgeOutput> CreateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md 扩展名；如 业务分析/测试KO 或 knowledge/业务分析/测试KO）")] string name,
        [Description("分类，自由字符串：流程 / 数据源 / 规则 / Entity 等")] string kind,
        [Description("markdown 正文：人类说明、字段表、SQL 模板、示例输出等")] string body,
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"targetId\":\"Hive表/fin_dw\"}]")] string? linksJson = null,
        [Description("front-matter 额外字段（可选），JSON 对象，标量值，如 {\"subject\":\"…\",\"status\":\"active\"}（Intelligence 生命周期等语料自有元数据）")] string? extraJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new CreateDocumentKnowledgeOutput { Error = "未配置资料库目录。" };

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
                ExtraFrontMatter = ParseExtras(extraJson),
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

    [Description("更新一个文档知识对象（.md 文件）的 kind/body/links/额外字段。按相对路径定位（仅可更新 labs knowledge/ 子树内的知识对象，" +
                 "路径越界拒绝）；传哪个参数更新哪个，不传保留原值。links 全量替换；extraJson 按键合并（只覆盖给出的键，" +
                 "其余 front-matter 额外字段原样保留——如只改 status 时不会丢 subject/claim）。name=相对路径（不含 .md）。")]
    public async Task<UpdateDocumentKnowledgeOutput> UpdateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 knowledge/业务分析/退款 或 业务分析/退款）")] string name,
        [Description("新 kind（可选，不传保留原值）")] string? kind = null,
        [Description("新 markdown 正文（可选，不传保留原值）")] string? body = null,
        [Description("新 links（可选，JSON 数组，全量替换；不传保留原 links）。targetId=相对路径权威 或 target=名称")] string? linksJson = null,
        [Description("front-matter 额外字段按键合并（可选，JSON 对象标量值，如 {\"status\":\"superseded\",\"last_updated\":\"2026-10-01\"}）；未提到的额外键原样保留")] string? extraJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new UpdateDocumentKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new UpdateDocumentKnowledgeOutput { Error = "未配置资料库目录。" };

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
            // 额外字段按键合并（未提到的键经透传原样保留）
            if (extraJson is not null && ParseExtras(extraJson) is { } patch)
            {
                def.ExtraFrontMatter ??= new Dictionary<string, object?>();
                foreach (var (key, value) in patch) def.ExtraFrontMatter[key] = value;
            }

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

    // —— 知识对象库体检 ——

    public sealed class ValidateKnowledgeOutput
    {
        /// <summary>体检的知识对象文件数。</summary>
        public int TotalChecked { get; set; }
        public List<KnowledgeDiagnosticDto> Diagnostics { get; set; } = new();
        /// <summary>分类汇总（类别 → 条数），如「断链: 2」。</summary>
        public string? Summary { get; set; }
        public string? Error { get; set; }
    }

    public sealed class KnowledgeDiagnosticDto
    {
        public string? Severity { get; set; } // error（断链）
        public string? Category { get; set; } // 断链
        public string? File { get; set; }
        public string? Message { get; set; }
    }

    [Description("体检知识对象库：全量解析知识对象 .md 文件，检查断链（links 指向不存在的文件——改名/移动/删除目标后遗留的坏引用，error 级）。" +
                 "写完/改完知识对象后自检调用（尤其 Rename/Merge 改写引用后复查）。诊断不阻断写入。")]
    public async Task<ValidateKnowledgeOutput> ValidateKnowledge(CancellationToken cancellationToken = default)
    {
        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new ValidateKnowledgeOutput { Error = "未配置资料库目录。" };

        try
        {
            return await Task.Run(() =>
            {
                var (knowledgeRoot, _) = ResolveScope(root);

                // 全量收集（含解析失败项——参与断链索引）。
                var files = new List<(string Full, string Rel, KnowledgeObjectDefinition? Def)>();
                CollectOkFiles(knowledgeRoot, knowledgeRoot, files, cancellationToken);
                var byRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // rel → full
                foreach (var f in files)
                    byRel[f.Rel] = f.Full;

                // 解析委托：与 Read 路径完全同源（ResolveTargetWithPath：路径候选 + 裸名全局兜底 + 目录/README 兜底），
                // 防止校验器与读写工具解析语义分叉产生假阳性断链。
                KnowledgeItem? Resolve(string target, string sourceRelPath)
                {
                    if (!byRel.TryGetValue(sourceRelPath, out var sourceFull)) return null;
                    var dir = Path.GetDirectoryName(sourceFull);
                    var resolved = OkTargetResolver.ResolveTargetWithPath(_parser, target, root, dir);
                    return resolved?.FullPath is null
                        ? null // 断链
                        : new KnowledgeItem(ToRelativePath(knowledgeRoot, resolved.FullPath), resolved.Def);
                }

                var items = files.Select(f => new KnowledgeItem(f.Rel, f.Def)).ToList();
                var diagnostics = KnowledgeGraphValidator.Validate(items, Resolve);

                var output = new ValidateKnowledgeOutput
                {
                    TotalChecked = files.Count,
                    Diagnostics = diagnostics.Select(d => new KnowledgeDiagnosticDto
                    {
                        Severity = d.Severity, Category = d.Category, File = d.File, Message = d.Message,
                    }).ToList(),
                };
                output.Summary = diagnostics.Count == 0
                    ? "全部通过（0 诊断）"
                    : string.Join(" / ", diagnostics.GroupBy(d => d.Category)
                        .Select(g => $"{g.Key}: {g.Count()}"));
                return output;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ValidateKnowledgeOutput { Error = ex.Message };
        }
    }

    /// <summary>递归收集知识对象文件（.md，复用 List 的目录守卫：跳构建产物/VCS/依赖目录 + 符号链接，上限 2000）。
    /// 与 <see cref="EnumerateOkFiles"/> 的差异：收集 (全路径, 相对路径, 定义) 三元组供体检测算。</summary>
    private void CollectOkFiles(string dir, string knowledgeRoot, List<(string Full, string Rel, KnowledgeObjectDefinition? Def)> files, CancellationToken ct)
    {
        if (files.Count >= 2000) return;
        string[]? subdirs = null;
        string[]? mdFiles = null;
        try
        {
            mdFiles = Directory.GetFiles(dir, "*.md");
            subdirs = Directory.GetDirectories(dir);
        }
        catch { /* 无权限/不存在 → 跳过 */ }

        if (mdFiles is not null)
        {
            foreach (var f in mdFiles)
            {
                if (files.Count >= 2000) return;
                ct.ThrowIfCancellationRequested();
                KnowledgeObjectDefinition? def = null;
                try { _ = _parser.TryParse(File.ReadAllText(f), out def, out _); }
                catch { /* 读/解析失败 → 仍收集（Def=null，参与断链索引） */ }
                files.Add((f, ToRelativePath(knowledgeRoot, f), def));
            }
        }
        if (subdirs is null) return;
        foreach (var d in subdirs)
        {
            if (files.Count >= 2000) return;
            var name = Path.GetFileName(d);
            if (IsIgnoredDir(name) || IsSymlinkDir(d)) continue;
            CollectOkFiles(d, knowledgeRoot, files, ct);
        }
    }

    // —— 搜索 / 关联 / 改名 / 合并（知识增长循环的检索面与治理面，2026-09-20） ——

    public sealed class KnowledgeSearchMatch
    {
        /// <summary>知识库根相对路径（不含 .md，Read/Search 入参用此）。</summary>
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        /// <summary>命中位置：name / kind / extras / body（可多命中，分号分隔）。</summary>
        public string? MatchedIn { get; set; }
        public string? BodyPreview { get; set; }
    }

    public sealed class SearchKnowledgeOutput
    {
        public List<KnowledgeSearchMatch> Matches { get; set; } = new();
        public int TotalScanned { get; set; }
        public string? Error { get; set; }
    }

    [Description("跨目录关键词搜索知识对象（冷启动循环的 Explore 步）：对知识库全部 .md 做**不区分大小写**子串匹配——" +
                 "名称 stem / kind / front-matter 额外字段值 / 正文。按命中位置排序（名称 > kind > 额外字段 > 正文），" +
                 "返回路径+kind+命中位置+正文预览。可选 kind 过滤（如 已知认知）、folder 限定子目录。命中后用 " +
                 "ReadDocumentKnowledgeObject 读全文。这是 ListDocumentKnowledgeObjects（按目录枚举）的检索互补。")]
    public async Task<SearchKnowledgeOutput> SearchKnowledge(
        [Description("关键词（不区分大小写子串匹配名称/kind/额外字段/正文）")] string query,
        [Description("kind 过滤（可选，如 已知认知/业务场景）")] string? kind = null,
        [Description("限定子目录（可选，知识库内相对路径，可带 knowledge/ 前缀）")] string? folder = null,
        [Description("返回上限（默认 20，最大 50）")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchKnowledgeOutput { Error = "query 不能为空" };
        limit = Math.Clamp(limit, 1, 50);

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new SearchKnowledgeOutput { Error = "未配置资料库目录。" };

        try
        {
            return await Task.Run(() =>
            {
                var (knowledgeRoot, isLabs) = ResolveScope(root);
                var baseDir = knowledgeRoot;
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var folderNorm = isLabs ? StripKnowledgePrefix(folder) : folder;
                    baseDir = Path.Combine(knowledgeRoot, folderNorm);
                }
                if (!Directory.Exists(baseDir))
                    return new SearchKnowledgeOutput { Error = $"目录不存在: {folder}" };

                var files = new List<(string Full, string Rel, KnowledgeObjectDefinition? Def)>();
                CollectOkFiles(baseDir, knowledgeRoot, files, cancellationToken);

                var q = query.Trim();
                var scored = new List<(int Score, string MatchedIn, KnowledgeSearchMatch Match)>();
                foreach (var (full, rel, def) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (kind is not null && !string.Equals(def?.Kind, kind, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = OkTargetResolver.StemOf(rel);
                    var matchedIn = new List<string>();
                    var score = 0;
                    if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) { matchedIn.Add("name"); score = 4; }
                    if (def?.Kind?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) { matchedIn.Add("kind"); score = Math.Max(score, 3); }
                    if (def?.ExtraFrontMatter is { } extras)
                    {
                        foreach (var v in extras.Values)
                            if (v?.ToString()?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                matchedIn.Add("extras");
                                score = Math.Max(score, 2);
                                break;
                            }
                    }
                    if (def?.Body?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) { matchedIn.Add("body"); score = Math.Max(score, 1); }
                    if (score == 0) continue;
                    scored.Add((score, string.Join(";", matchedIn), new KnowledgeSearchMatch
                    {
                        Path = rel,
                        Name = name,
                        Kind = def?.Kind,
                        MatchedIn = string.Join(";", matchedIn),
                        BodyPreview = string.IsNullOrWhiteSpace(def?.Body) ? null : Truncate(def!.Body, 80),
                    }));
                }

                return new SearchKnowledgeOutput
                {
                    TotalScanned = files.Count,
                    Matches = scored.OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Match.Path, StringComparer.OrdinalIgnoreCase)
                        .Take(limit)
                        .Select(s => s.Match)
                        .ToList(),
                };
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SearchKnowledgeOutput { Error = ex.Message };
        }
    }

    public sealed class RelatedLinkDto
    {
        public string? Role { get; set; }
        /// <summary>出边：目标 targetId 原文；入边：来源对象路径。</summary>
        public string? Target { get; set; }
        /// <summary>出边：目标的库内相对路径（解析失败=null 断链）。</summary>
        public string? ResolvedPath { get; set; }
    }

    public sealed class RelatedKnowledgeOutput
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        /// <summary>本对象发出的 links（谁被我引用）。</summary>
        public List<RelatedLinkDto> Outgoing { get; set; } = new();
        /// <summary>引用本对象的边（谁引用我——反向链接，合并/改名前查影响面）。</summary>
        public List<RelatedLinkDto> Incoming { get; set; } = new();
        public string? Error { get; set; }
    }

    [Description("查一个知识对象的关联：出边（它引用谁，含解析后路径）+ 入边（谁引用它，反向链接）。" +
                 "合并/改名/废弃对象前先查 Incoming 评估影响面；扩展已有认知时查 Outgoing 找可复用对象。name=相对路径（同 Read）。")]
    public async Task<RelatedKnowledgeOutput> RelatedKnowledge(
        [Description("相对路径（不含 .md；如 企业知识架构/2_Feature/同设备识别）")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new RelatedKnowledgeOutput { Error = "名称不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new RelatedKnowledgeOutput { Error = "未配置资料库目录。" };

        try
        {
            return await Task.Run(() =>
            {
                var (knowledgeRoot, isLabs) = ResolveScope(root);
                var focal = ResolveFileInScope(name, root, knowledgeRoot, isLabs);
                if (focal is null)
                    return new RelatedKnowledgeOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };

                var focalRel = ToRelativePath(knowledgeRoot, focal);
                string? focalKind = null;
                if (_parser.TryParse(File.ReadAllText(focal), out var focalDef, out _) && focalDef is not null)
                    focalKind = focalDef.Kind;
                var focalDir = Path.GetDirectoryName(focal)!;

                // 出边：本对象 links + 逐一解析
                var outgoing = new List<RelatedLinkDto>();
                if (focalDef?.Links is { } links)
                {
                    foreach (var l in links)
                    {
                        var resolved = OkTargetResolver.ResolveTargetWithPath(_parser, l.Target, root, focalDir);
                        outgoing.Add(new RelatedLinkDto
                        {
                            Role = l.Role,
                            Target = l.Target,
                            ResolvedPath = resolved?.FullPath is null ? null : ToRelativePath(knowledgeRoot, resolved.FullPath),
                        });
                    }
                }

                // 入边：全库扫描，解析每条 link，命中焦点全路径的收集
                var files = new List<(string Full, string Rel, KnowledgeObjectDefinition? Def)>();
                CollectOkFiles(knowledgeRoot, knowledgeRoot, files, cancellationToken);
                var incoming = new List<RelatedLinkDto>();
                foreach (var (full, rel, def) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(full, focal, StringComparison.OrdinalIgnoreCase)) continue;
                    if (def?.Links is not { } ls) continue;
                    var dir = Path.GetDirectoryName(full)!;
                    foreach (var l in ls)
                    {
                        var resolved = OkTargetResolver.ResolveTargetWithPath(_parser, l.Target, root, dir);
                        if (resolved?.FullPath is not null
                            && string.Equals(Path.GetFullPath(resolved.FullPath), Path.GetFullPath(focal), StringComparison.OrdinalIgnoreCase))
                        {
                            incoming.Add(new RelatedLinkDto { Role = l.Role, Target = rel });
                        }
                    }
                }

                return new RelatedKnowledgeOutput
                {
                    Path = focalRel,
                    Kind = focalKind,
                    Outgoing = outgoing,
                    Incoming = incoming,
                };
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new RelatedKnowledgeOutput { Error = ex.Message };
        }
    }

    public sealed class RenameKnowledgeOutput
    {
        public bool Success { get; set; }
        public string? NewPath { get; set; }
        /// <summary>被改写引用的文件数（入边全部改指新路径）。</summary>
        public int ReferencesRewritten { get; set; }
        public string? Error { get; set; }
    }

    [Description("改名/迁移一个知识对象（.md 文件对象）：移动文件 + **自动改写全部入边引用**（谁引用它，targetId 全部改指新路径——" +
                 "规范写法：同目录裸名/跨目录 knowledge/ 前缀根相对）。改名前建议先 RelatedKnowledge 查影响面；" +
                 "完成后建议 ValidateKnowledge 复查。场景目录对象（README.md 本体）不支持——目录整体迁移请手动 git mv 后跑 ValidateKnowledge。")]
    public async Task<RenameKnowledgeOutput> RenameKnowledge(
        [Description("现有相对路径（不含 .md；如 企业知识架构/2_Feature/同设备识别）")] string name,
        [Description("新相对路径（不含 .md，知识库内；改名=同目录新名，迁移=新目录路径）")] string newPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newPath))
            return new RenameKnowledgeOutput { Error = "name 与 newPath 都不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new RenameKnowledgeOutput { Error = "未配置资料库目录。" };

        try
        {
            var (knowledgeRoot, isLabs) = ResolveScope(root);
            var source = ResolveFileInScope(name, root, knowledgeRoot, isLabs);
            if (source is null)
                return new RenameKnowledgeOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };
            if (string.Equals(Path.GetFileName(source), "README.md", StringComparison.OrdinalIgnoreCase))
                return new RenameKnowledgeOutput { Error = "场景目录对象（README.md 本体）不支持改名——目录整体迁移请手动 git mv 后跑 ValidateKnowledge" };

            var newNameNoExt = StripKnowledgePrefix(StripOkExt(newPath));
            var target = Path.Combine(knowledgeRoot, newNameNoExt + ".md");
            if (File.Exists(target))
                return new RenameKnowledgeOutput { Error = $"目标已存在: {newNameNoExt}" };

            // 先改写入边（此时旧路径仍可解析），再移动文件——中途失败不丢引用
            var rewritten = await RewriteReferencesAsync(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(source) },
                Path.GetFullPath(target), root, knowledgeRoot, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);

            return new RenameKnowledgeOutput
            {
                Success = true,
                NewPath = ToRelativePath(root, target),
                ReferencesRewritten = rewritten,
            };
        }
        catch (Exception ex)
        {
            return new RenameKnowledgeOutput { Error = ex.Message };
        }
    }

    public sealed class MergeKnowledgeOutput
    {
        public bool Success { get; set; }
        /// <summary>合并产物（新对象）路径。</summary>
        public string? MergedPath { get; set; }
        /// <summary>被合并的原对象（已标 superseded，保留作 lineage）。</summary>
        public List<string>? Superseded { get; set; }
        public int ReferencesRewritten { get; set; }
        public string? Error { get; set; }
    }

    [Description("合并两个表达同一概念的知识对象（Knowledge Marriage）：生成新对象 C（front-matter 带 parents=[primary,secondary] 溯源），" +
                 "links 取两者并集（去重、剔自指），原对象标 status: superseded + superseded_by=C **保留不删**（lineage/provenance），" +
                 "全部入边引用自动改指 C。mergedBody 由 agent 融合两对象正文写成（先 Read 两者再合并）。合并前建议 RelatedKnowledge 查两者影响面；" +
                 "完成后 ValidateKnowledge 复查。")]
    public async Task<MergeKnowledgeOutput> MergeKnowledge(
        [Description("对象 A 相对路径（不含 .md）——其 kind 作为合并对象默认 kind")] string primary,
        [Description("对象 B 相对路径（不含 .md）")] string secondary,
        [Description("合并产物新相对路径（不含 .md，知识库内，须不存在）")] string newName,
        [Description("融合后的 markdown 正文（agent 融合 A/B 内容写成）")] string mergedBody,
        [Description("合并对象 kind（可选，默认取 A 的 kind）")] string? kind = null,
        [Description("合并对象额外 front-matter 字段（可选，JSON 对象，如 Intelligence 生命周期字段）")] string? extraJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(secondary) || string.IsNullOrWhiteSpace(newName))
            return new MergeKnowledgeOutput { Error = "primary/secondary/newName 都不能为空" };

        var root = _knowledgeDir.CurrentDirectory ?? _knowledgeDir.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root))
            return new MergeKnowledgeOutput { Error = "未配置资料库目录。" };

        try
        {
            var (knowledgeRoot, isLabs) = ResolveScope(root);
            var aFull = ResolveFileInScope(primary, root, knowledgeRoot, isLabs);
            var bFull = ResolveFileInScope(secondary, root, knowledgeRoot, isLabs);
            if (aFull is null) return new MergeKnowledgeOutput { Error = $"找不到 primary: {primary}" };
            if (bFull is null) return new MergeKnowledgeOutput { Error = $"找不到 secondary: {secondary}" };
            if (string.Equals(aFull, bFull, StringComparison.OrdinalIgnoreCase))
                return new MergeKnowledgeOutput { Error = "primary 与 secondary 是同一对象" };

            var newNameNoExt = StripKnowledgePrefix(StripOkExt(newName));
            var cFull = Path.Combine(knowledgeRoot, newNameNoExt + ".md");
            if (File.Exists(cFull))
                return new MergeKnowledgeOutput { Error = $"目标已存在: {newNameNoExt}" };

            if (!_parser.TryParse(File.ReadAllText(aFull), out var aDef, out var aErr) || aDef is null)
                return new MergeKnowledgeOutput { Error = $"primary 解析失败: {aErr}" };
            if (!_parser.TryParse(File.ReadAllText(bFull), out var bDef, out var bErr) || bDef is null)
                return new MergeKnowledgeOutput { Error = $"secondary 解析失败: {bErr}" };

            // C 的 links：A+B 并集（role+target 去重；剔除解析后指向 A/B 自身的边——自指无意义）
            var oldFulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(aFull), Path.GetFullPath(bFull),
            };
            var links = new List<KnowledgeLink>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in new[] { aDef, bDef })
            {
                foreach (var l in src.Links)
                {
                    if (string.IsNullOrWhiteSpace(l.Target)) continue;
                    var resolved = OkTargetResolver.ResolveTargetWithPath(_parser, l.Target, root,
                        Path.GetDirectoryName(src == aDef ? aFull! : bFull!)!);
                    if (resolved?.FullPath is { } rf && oldFulls.Contains(Path.GetFullPath(rf))) continue;
                    if (!seen.Add($"{l.Role}|{l.Target}")) continue;
                    links.Add(new KnowledgeLink { Role = l.Role, Target = l.Target, Expand = l.Expand });
                }
            }

            // C 的 front-matter：parents 溯源 + 可选生命周期字段（extraJson）
            var extras = new Dictionary<string, object?>
            {
                ["parents"] = new List<object?> { ToRelativePath(root, aFull), ToRelativePath(root, bFull) },
            };
            if (ParseExtras(extraJson) is { } patch)
                foreach (var (k, v) in patch) extras[k] = v;

            var cDef = new KnowledgeObjectDefinition
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? aDef.Kind : kind,
                Body = mergedBody ?? string.Empty,
                Links = links,
                ExtraFrontMatter = extras,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(cFull)!);
            await File.WriteAllTextAsync(cFull, _parser.Serialize(cDef), cancellationToken);

            // A/B 标 superseded + superseded_by=C（保留不删，lineage）
            foreach (var (full, def) in new[] { (aFull, aDef), (bFull, bDef) })
            {
                def.ExtraFrontMatter ??= new Dictionary<string, object?>();
                def.ExtraFrontMatter["status"] = "superseded";
                def.ExtraFrontMatter["superseded_by"] = ToRelativePath(root, cFull);
                await File.WriteAllTextAsync(full, _parser.Serialize(def), cancellationToken);
            }

            // 入边引用 A/B → C
            var rewritten = await RewriteReferencesAsync(oldFulls, Path.GetFullPath(cFull), root, knowledgeRoot, cancellationToken)
                .ConfigureAwait(false);

            return new MergeKnowledgeOutput
            {
                Success = true,
                MergedPath = ToRelativePath(root, cFull),
                Superseded = new List<string> { ToRelativePath(root, aFull), ToRelativePath(root, bFull) },
                ReferencesRewritten = rewritten,
            };
        }
        catch (Exception ex)
        {
            return new MergeKnowledgeOutput { Error = ex.Message };
        }
    }

    /// <summary>按 name 解析知识对象文件全路径（labs 根相对 + knowledge/ 相对候选 + 越界守卫）——Read/Update 同款语义。</summary>
    private string? ResolveFileInScope(string name, string root, string knowledgeRoot, bool isLabs)
    {
        var candidates = OkTargetResolver.CandidateOkPaths(name, root, root).ToList();
        if (isLabs)
            candidates.AddRange(OkTargetResolver.CandidateOkPaths(StripKnowledgePrefix(name), knowledgeRoot, knowledgeRoot));
        return candidates.FirstOrDefault(p => File.Exists(p) && IsUnder(p, knowledgeRoot));
    }

    /// <summary>把全库中解析到 <paramref name="oldFullPaths"/> 的 link targetId 改写为指向 <paramref name="newFullPath"/>
    /// 的规范写法（<see cref="KnowledgeLinkRewriter.CanonicalTargetId"/>：同目录裸名/跨目录 knowledge/ 前缀根相对）。
    /// 返回被改写的文件数。front-matter 经透传 round-trip（额外字段不丢）。</summary>
    private async Task<int> RewriteReferencesAsync(
        HashSet<string> oldFullPaths, string newFullPath, string root, string knowledgeRoot, CancellationToken ct)
    {
        var files = new List<(string Full, string Rel, KnowledgeObjectDefinition? Def)>();
        CollectOkFiles(knowledgeRoot, knowledgeRoot, files, ct);

        var rewritten = 0;
        foreach (var (full, _, def) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (def?.Links is not { Count: > 0 }) continue;
            var dir = Path.GetDirectoryName(full)!;
            var changed = false;
            foreach (var l in def.Links)
            {
                var resolved = OkTargetResolver.ResolveTargetWithPath(_parser, l.Target, root, dir);
                if (resolved?.FullPath is not { } rf) continue;
                if (!oldFullPaths.Contains(Path.GetFullPath(rf))) continue;
                l.Target = KnowledgeLinkRewriter.CanonicalTargetId(full, newFullPath, root);
                changed = true;
            }
            if (!changed) continue;
            await File.WriteAllTextAsync(full, _parser.Serialize(def), ct);
            rewritten++;
        }
        return rewritten;
    }

    /// <summary>解析 extraJson（front-matter 额外字段 JSON 对象）。仅标量（string/number/bool/null）；
    /// 嵌套结构跳过（front-matter 是 YAML，嵌套 JSON 原样写回会产非法 YAML）。非法/空返 null。</summary>
    private static Dictionary<string, object?>? ParseExtras(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(extraJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),   // 数字原样文本（同是合法 YAML 标量）
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => null,                                       // 嵌套对象/数组跳过
                };
            }
            return dict.Count > 0 ? dict : null;
        }
        catch
        {
            return null; // 容错：extraJson 非法 → 忽略额外字段，仍执行主体创建/更新
        }
    }

    // —— 内部助手 ——

    /// <summary>解析 links JSON。文档版 <c>targetId</c>=相对路径（无 notes DB 反查）。
    /// <c>targetId</c> 优先 → <see cref="KnowledgeLink.Target"/>=路径；回退 <c>target</c>（名称）。两者都无跳过。
    /// 解析失败返空列表（容错，镜像 note）。<see cref="KnowledgeLink.TargetId"/> 留 null——
    /// 与 <see cref="DocumentKnowledgeSourceParser.ToDomainLink"/> 同构（序列化把 Target 写回 targetId 字段）。
    /// <c>expand:true</c> 透传 <see cref="KnowledgeLink.Expand"/>（作者标记「该目标内容 AI 读取时内联」）。</summary>
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
                var expand = el.TryGetProperty("expand", out var exEl) && exEl.GetBoolean();
                result.Add(new KnowledgeLink { Role = role, Target = target, TargetId = null, Expand = expand });
            }
        }
        catch { /* 容错：links JSON 非法 → 返回空 links，仍创建/更新知识对象 */ }
        return result;
    }

    /// <summary>组装：path + kind + name + 正文 + links（标了 <see cref="KnowledgeLink.Expand"/> 的边递归内联目标内容）。
    /// 委托 <see cref="KnowledgeExpansionBuilder"/>（纯函数，可单测），解析目标经 <see cref="OkTargetResolver.ResolveTargetWithPath"/>
    /// （一次取齐 显示名+全路径+定义，全路径做去重防环键）。
    /// <para><b>防上下文溢出</b>：link 可能指向含完整字段表/SQL/示例的大 .md，多目标内联会撑爆 LLM 上下文窗口
    /// （曾触发 GLM <c>ResponseEnded</c> 提前终止 → "The response ended prematurely"）。故每目标正文内联有上限、累计有上限，
    /// 递归深度有硬上限——超出截断头部 + 标记并告知 agent 如何取完整内容，同 <c>GrepTool</c>/<c>run_in_terminal</c> 的 Truncated 范式。</para></summary>
    private string Format(KnowledgeObjectDefinition def, string relPath, string root, string? focalFullPath, int? expandDepth)
        => KnowledgeExpansionBuilder.BuildExpandedDefinition(
            def, relPath, focalFullPath,
            (t, dir) => OkTargetResolver.ResolveTargetWithPath(_parser, t, root, dir),
            expandDepth);

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
