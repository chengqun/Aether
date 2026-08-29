using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Mobile.Shared.Hosting.Tools;

/// <summary>
/// 手机端文档版知识对象（<c>.md</c> 文件）AI 工具，提供 4 工具（List/Read/Create/Update），
/// 镜像桌面 <c>DocumentKnowledgeTool</c> 但**作用域简化为 labs <c>knowledge/</c> 子树**：
/// 手机端知识库根 = labs（<c>Func&lt;string?&gt;</c> 已注册，闭包返回 <c>&lt;tenantDir&gt;/labs</c>），
/// 无独立 <c>IKnowledgeDirectoryProvider</c>/<c>OkTargetResolver</c>（桌面-only）。link 解析用相对路径（target 字符串），
/// 与 <c>KnowledgeCreateViewModel</c> 同构——文档版 <c>targetId</c>=相对路径，<c>TargetId</c> 留 null。
/// </summary>
public sealed class MobileDocumentKnowledgeTool
{
    private readonly Func<string?> _labsRoot;
    private readonly IDocumentKnowledgeSourceParser _parser;

    private static readonly string KnowledgeDir =
        CatalogNoteDirectory.For(Acme.Notes.Domain.NoteNodeType.Knowledge).DirectoryName; // "knowledge"

    public MobileDocumentKnowledgeTool(Func<string?> labsRoot, IDocumentKnowledgeSourceParser parser)
    {
        _labsRoot = labsRoot;
        _parser = parser;
    }

    private string? KnowledgeRoot()
    {
        var labs = _labsRoot();
        if (string.IsNullOrWhiteSpace(labs)) return null;
        return Path.Combine(labs, KnowledgeDir);
    }

    // —— DTO ——

    public sealed class Summary
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? BodyPreview { get; set; }
    }

    public sealed class ListOutput
    {
        public List<Summary> KnowledgeObjects { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class ReadOutput
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Definition { get; set; }
        public string? Error { get; set; }
    }

    public sealed class MutateOutput
    {
        public bool Success { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    // —— 工具方法 ——

    [Description("列出知识对象（labs/knowledge/*.md 文件，磁盘上的知识资产）。返回相对路径/名称/kind/正文预览。" +
                 "可选 folder 只列指定子目录（knowledge 目录内的相对路径，如 业务分析）。总上限 200。")]
    public Task<ListOutput> ListDocumentKnowledgeObjects(
        [Description("knowledge 目录内的子目录相对路径（可选；如 业务分析）")] string? folder = null,
        CancellationToken cancellationToken = default)
    {
        var root = KnowledgeRoot();
        if (root is null)
            return Task.FromResult(new ListOutput { Error = "未配置 labs 目录。" });

        var baseDir = string.IsNullOrWhiteSpace(folder) ? root : Path.Combine(root, folder!);
        if (!Directory.Exists(baseDir))
            return Task.FromResult(new ListOutput { Error = $"目录不存在: {folder}" });

        try
        {
            var list = Task.Run(() =>
            {
                var result = new List<Summary>();
                Enumerate(baseDir, result, 200, cancellationToken);
                return result;
            }, cancellationToken);
            return list.ContinueWith(t => new ListOutput { KnowledgeObjects = t.Result },
                cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ListOutput { Error = ex.Message });
        }
    }

    [Description("按相对路径读取一个知识对象（labs/knowledge/*.md）的完整定义：kind + 正文 + links 指向的子 .md 文件内容（只展开一层）。" +
                 "name=相对路径不含 .md（如 业务分析/退款 或 knowledge/业务分析/退款，两种写法均可）。先调此工具获取知识再执行任务。" +
                 "link 解析：targetId/target 是相对路径，优先知识库根相对（knowledge/ 前缀），后回退 knowledge 目录内同名文件。")]
    public Task<ReadOutput> ReadDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 业务分析/退款 或 knowledge/业务分析/退款；也可带 .md 后缀）")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new ReadOutput { Error = "名称不能为空" });

        var knowledgeRoot = KnowledgeRoot();
        if (knowledgeRoot is null)
            return Task.FromResult(new ReadOutput { Error = "未配置 labs 目录。" });
        var labs = _labsRoot()!;

        return Task.Run(() =>
        {
            var full = ResolveFile(name, knowledgeRoot, labs);
            if (full is null)
                return new ReadOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };

            string raw;
            try { raw = File.ReadAllText(full); }
            catch (Exception ex) { return new ReadOutput { Error = $"读取失败: {ex.Message}" }; }

            if (!_parser.TryParse(raw, out var def, out var err) || def is null)
                return new ReadOutput { Error = $"解析失败: {err}" };

            var relPath = ToRelative(labs, full);
            return new ReadOutput
            {
                Path = relPath,
                Name = StemOf(relPath),
                Kind = def.Kind,
                Definition = Format(def, relPath, knowledgeRoot),
            };
        }, cancellationToken);
    }

    [Description("创建一个知识对象（labs/knowledge/<name>.md 文件）。kind 分类自由字符串（流程/数据源/规则 等）。" +
                 "links 可选 JSON 数组：[{\"role\":\"uses\",\"targetId\":\"业务分析/某表\"}]（targetId=相对路径，指向 knowledge/ 下另一 .md）。已存在则失败。")]
    public Task<MutateOutput> CreateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md 扩展名；如 业务分析/测试KO，可带 knowledge/ 前缀）")] string name,
        [Description("分类，自由字符串：流程 / 数据源 / 规则 等")] string kind,
        [Description("markdown 正文：人类说明、字段表、SQL 模板、示例输出等")] string body,
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"targetId\":\"业务分析/某表\"}]")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new MutateOutput { Error = "名称不能为空" });

        var knowledgeRoot = KnowledgeRoot();
        if (knowledgeRoot is null)
            return Task.FromResult(new MutateOutput { Error = "未配置 labs 目录。" });
        var labs = _labsRoot()!;

        var nameNoExt = StripKnowledgePrefix(StripMdExt(name.Trim()));
        if (string.IsNullOrWhiteSpace(nameNoExt))
            return Task.FromResult(new MutateOutput { Error = "名称不能为空" });

        var full = Path.Combine(knowledgeRoot, nameNoExt + ".md");
        if (File.Exists(full))
            return Task.FromResult(new MutateOutput { Error = $"已存在: {nameNoExt}" });

        var def = new KnowledgeObjectDefinition
        {
            Kind = kind ?? string.Empty,
            Body = body ?? string.Empty,
            Links = ParseLinks(linksJson),
        };
        try
        {
            var source = _parser.Serialize(def);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, source);
            var relPath = ToRelative(labs, full);
            return Task.FromResult(new MutateOutput { Success = true, Path = relPath, Name = StemOf(relPath) });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MutateOutput { Error = ex.Message });
        }
    }

    [Description("更新一个知识对象（labs/knowledge/*.md）的 kind/body/links。按相对路径定位；传哪个参数更新哪个，不传保留原值。links 全量替换。")]
    public Task<MutateOutput> UpdateDocumentKnowledgeObject(
        [Description("相对路径（不含 .md；如 业务分析/退款）")] string name,
        [Description("新 kind（可选，不传保留原值）")] string? kind = null,
        [Description("新 markdown 正文（可选，不传保留原值）")] string? body = null,
        [Description("新 links（可选，JSON 数组，全量替换；不传保留原 links）。targetId=相对路径 或 target=名称")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new MutateOutput { Error = "名称不能为空" });

        var knowledgeRoot = KnowledgeRoot();
        if (knowledgeRoot is null)
            return Task.FromResult(new MutateOutput { Error = "未配置 labs 目录。" });
        var labs = _labsRoot()!;

        return Task.Run(() =>
        {
            var full = ResolveFile(name, knowledgeRoot, labs);
            if (full is null)
                return new MutateOutput { Error = $"找不到知识对象文件（或不在 knowledge/ 子树内）: {name}" };

            string raw;
            try { raw = File.ReadAllText(full); }
            catch (Exception ex) { return new MutateOutput { Error = $"读取失败: {ex.Message}" }; }

            if (!_parser.TryParse(raw, out var def, out var err) || def is null)
                return new MutateOutput { Error = $"解析失败: {err}" };

            if (kind is not null) def.Kind = kind;
            if (body is not null) def.Body = body;
            if (linksJson is not null) def.Links = ParseLinks(linksJson);

            try
            {
                File.WriteAllText(full, _parser.Serialize(def));
                var relPath = ToRelative(labs, full);
                return new MutateOutput { Success = true, Path = relPath, Name = StemOf(relPath) };
            }
            catch (Exception ex) { return new MutateOutput { Error = ex.Message }; }
        }, cancellationToken);
    }

    // —— 内部助手 ——

    private string? ResolveFile(string name, string knowledgeRoot, string labs)
    {
        var nameNoExt = StripKnowledgePrefix(StripMdExt(name.Trim()));
        // 候选：knowledge 目录相对 + labs 根相对（支持带 knowledge/ 前缀的写法）。
        var candidates = new[]
        {
            Path.Combine(knowledgeRoot, nameNoExt + ".md"),
            Path.Combine(labs, name.Trim().EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name.Trim() : name.Trim() + ".md"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c) && IsUnder(c, knowledgeRoot))
                return c;
        }
        return null;
    }

    private string Format(KnowledgeObjectDefinition def, string relPath, string knowledgeRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"path: {relPath}");
        sb.AppendLine($"kind: {def.Kind}");
        sb.AppendLine($"name: {StemOf(relPath)}");
        if (!string.IsNullOrWhiteSpace(def.Body))
            sb.AppendLine().AppendLine(def.Body);

        if (def.Links.Count > 0)
        {
            sb.AppendLine().AppendLine("links:");
            foreach (var l in def.Links)
            {
                sb.AppendLine($"[{l.Role}] {l.Target} (path:{l.Target}):");
                var targetFile = Path.Combine(knowledgeRoot, StripKnowledgePrefix(StripMdExt(l.Target)) + ".md");
                if (File.Exists(targetFile))
                {
                    try
                    {
                        var raw = File.ReadAllText(targetFile);
                        // 只内联一层，不递归；内联目标原文（缩进 2 格）。
                        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
                            sb.AppendLine("  " + line);
                    }
                    catch { sb.AppendLine("  （找不到知识对象文件）"); }
                }
                else
                {
                    sb.AppendLine("  （找不到知识对象文件）");
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

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
                var target = "";
                if (el.TryGetProperty("targetId", out var tidEl))
                    target = tidEl.GetString() ?? "";
                else if (el.TryGetProperty("target", out var tEl))
                    target = tEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(target)) continue;
                result.Add(new KnowledgeLink { Role = role, Target = target, TargetId = null });
            }
        }
        catch { /* 容错：links JSON 非法 → 空 links */ }
        return result;
    }

    private void Enumerate(string dir, List<Summary> list, int limit, CancellationToken ct)
    {
        if (list.Count >= limit) return;
        string[] files, subdirs;
        try
        {
            files = Directory.GetFiles(dir, "*.md");
            subdirs = Directory.GetDirectories(dir);
        }
        catch { return; }

        foreach (var f in files)
        {
            if (list.Count >= limit) return;
            ct.ThrowIfCancellationRequested();
            KnowledgeObjectDefinition? def = null;
            try { _ = _parser.TryParse(File.ReadAllText(f), out def, out _); }
            catch { /* 读/解析失败 → 仍列出 */ }
            var labs = _labsRoot()!;
            var rel = ToRelative(labs, f);
            list.Add(new Summary
            {
                Path = rel,
                Name = StemOf(rel),
                Kind = def?.Kind,
                BodyPreview = string.IsNullOrWhiteSpace(def?.Body) ? null : Truncate(def!.Body, 80),
            });
        }

        foreach (var d in subdirs)
        {
            if (list.Count >= limit) return;
            var dn = Path.GetFileName(d);
            if (dn.StartsWith(".") || IsSymlinkDir(d)) continue;
            Enumerate(d, list, limit, ct);
        }
    }

    private static bool IsSymlinkDir(string fullPath)
    {
        try { return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

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

    private static string ToRelative(string root, string full)
    {
        try { return Path.GetRelativePath(root, full); }
        catch { return full; }
    }

    private static string StemOf(string relPath)
    {
        var fileName = Path.GetFileName(relPath);
        return StripMdExt(fileName);
    }

    private static string StripMdExt(string path)
        => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && path.Length > 3
            ? path.Substring(0, path.Length - 3)
            : path;

    private static string StripKnowledgePrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var norm = path.Replace('\\', '/');
        var prefix = KnowledgeDir + "/";
        if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path[prefix.Length..];
        return path;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
