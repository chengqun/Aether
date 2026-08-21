using System.ComponentModel;
using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Notes.AI.Tools;

/// <summary>
/// 让 Agent 读取知识对象（KnowledgeObject）的工具（设计文档"AI As Consumer"最小形态）。
/// ReadKnowledgeObject 按 links 把子笔记内容组装返回（typed link + 正文），agent 据此推理执行。
/// </summary>
public class KnowledgeTool
{
    private readonly INoteService _notes;
    private readonly IKnowledgeSourceParser _parser;

    public KnowledgeTool(INoteService notes, IKnowledgeSourceParser parser)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public class KnowledgeSummary
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? BodyPreview { get; set; }
    }

    public class ListKnowledgeOutput
    {
        public List<KnowledgeSummary> KnowledgeObjects { get; set; } = new();
        public string? Error { get; set; }
    }

    public class ReadKnowledgeOutput
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        /// <summary>组装后的完整知识（kind + 正文 + 各 link 指向的子笔记内容）。</summary>
        public string? Definition { get; set; }
        public string? Error { get; set; }
    }

    public class CreateKnowledgeOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    public class UpdateKnowledgeOutput
    {
        public bool Success { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出知识对象（KnowledgeObject）。返回名称、kind、正文预览。可选 parentId 只列指定 folder 下的 KO，不传则列全部。")]
    public async Task<ListKnowledgeOutput> ListKnowledgeObjects(
        [Description("父节点 id（可选，只列该 folder 下的知识对象；不传列全部）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _notes.GetAllAsync(cancellationToken);
            var query = all.Where(n => n.NodeType == NoteNodeType.Knowledge);
            if (!string.IsNullOrWhiteSpace(parentId))
                query = query.Where(n => string.Equals(n.ParentId, parentId, StringComparison.OrdinalIgnoreCase));
            var result = new List<KnowledgeSummary>();
            foreach (var note in query)
            {
                if (!_parser.TryParse(note.Content, out var def, out _)) continue;
                result.Add(new KnowledgeSummary
                {
                    Id = note.Id,
                    Name = note.Title,
                    Kind = def?.Kind,
                    BodyPreview = string.IsNullOrWhiteSpace(def?.Body) ? null : Truncate(def.Body, 80),
                });
            }
            return new ListKnowledgeOutput { KnowledgeObjects = result };
        }
        catch (Exception ex)
        {
            return new ListKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("按名称读取一个知识对象的完整定义：kind + 正文 + links 指向的子笔记内容。先调此工具获取知识，再按其模型执行任务。")]
    public async Task<ReadKnowledgeOutput> ReadKnowledgeObject(
        [Description("知识对象名称（笔记标题）")] string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ReadKnowledgeOutput { Error = "名称不能为空" };

            var all = await _notes.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.NodeType == NoteNodeType.Knowledge
                && string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));
            if (note is null)
                return new ReadKnowledgeOutput { Error = $"找不到知识对象: {name}" };

            if (!_parser.TryParse(note.Content, out var def, out var err) || def is null)
                return new ReadKnowledgeOutput { Error = $"解析失败: {err}" };

            return new ReadKnowledgeOutput
            {
                Id = note.Id,
                Name = note.Title,
                Kind = def.Kind,
                Definition = await FormatAsync(def, note.Id, note.Title, all, cancellationToken),
            };
        }
        catch (Exception ex)
        {
            return new ReadKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("创建一个知识对象（Knowledge 笔记节点）。用于沉淀可复用的流程/数据源/规则等资产。创建后可用 ReadKnowledgeObject 读取。")]
    public async Task<CreateKnowledgeOutput> CreateKnowledgeObject(
        [Description("知识对象名称（笔记标题，需唯一）")] string name,
        [Description("分类，自由字符串：流程 / 数据源 / 规则 / Entity 等")] string kind,
        [Description("markdown 正文：人类说明、字段表、SQL 模板、示例输出等")] string body,
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"targetId\":\"<guid>\"}] 或 [{\"role\":\"uses\",\"target\":\"情报分析表\"}]。优先 targetId（真实节点 id，权威，改名不断链）；回退 target（标题，按标题解析为 id）。先用 ListKnowledgeObjects/ReadKnowledgeObject 取目标 id")] string? linksJson = null,
        [Description("父节点 id（可选，不传则建到根节点下）。要建到指定 folder 下时，先用 ListNotes 查目标 folder 的 id 再传入")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new CreateKnowledgeOutput { Error = "名称不能为空" };

            var all = await _notes.GetAllAsync(cancellationToken);
            var links = ParseLinks(linksJson, all);
            var def = new KnowledgeObjectDefinition
            {
                Kind = kind ?? string.Empty,
                Body = body ?? string.Empty,
                Links = links,
            };
            var source = _parser.Serialize(def);
            var dto = await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = name.Trim(),
                Content = source,
                NodeType = NoteNodeType.Knowledge,
                ParentId = parentId,
            }, cancellationToken);
            return new CreateKnowledgeOutput { Success = true, Id = dto.Id, Name = dto.Title };
        }
        catch (Exception ex)
        {
            return new CreateKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("更新一个知识对象的 kind/body/links。按名称定位；传哪个参数就更新哪个，不传的保留原值。用于迭代调整知识对象。")]
    public async Task<UpdateKnowledgeOutput> UpdateKnowledgeObject(
        [Description("知识对象名称（笔记标题）")] string name,
        [Description("新 kind（可选，不传保留原值）")] string? kind = null,
        [Description("新 markdown 正文（可选，不传保留原值）")] string? body = null,
        [Description("新 links（可选，JSON 数组，全量替换原 links；不传保留原 links）。支持 targetId（真实节点 id，权威）或 target（标题）；如 [{\"role\":\"uses\",\"targetId\":\"<guid>\"}]")] string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new UpdateKnowledgeOutput { Error = "名称不能为空" };

            var all = await _notes.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.NodeType == NoteNodeType.Knowledge
                && string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));
            if (note is null)
                return new UpdateKnowledgeOutput { Error = $"找不到知识对象: {name}" };

            if (!_parser.TryParse(note.Content ?? "", out var def, out var err) || def is null)
                return new UpdateKnowledgeOutput { Error = $"解析失败: {err}" };

            // 按提供的参数部分更新（不传的保留原值）
            if (kind is not null) def.Kind = kind;
            if (body is not null) def.Body = body;
            if (linksJson is not null) def.Links = ParseLinks(linksJson, all);

            var source = _parser.Serialize(def);
            await _notes.UpdateAsync(new UpdateNoteRequest
            {
                Id = note.Id,
                Title = note.Title,       // 标题不变
                Content = source,
                NodeType = NoteNodeType.Knowledge,
            }, cancellationToken);
            return new UpdateKnowledgeOutput { Success = true, Name = note.Title };
        }
        catch (Exception ex)
        {
            return new UpdateKnowledgeOutput { Error = ex.Message };
        }
    }

    /// <summary>解析 links JSON。优先 <c>targetId</c>（真实节点 id，权威，改名不断链，按 id 反查标题）；
    /// 回退 <c>target</c>（标题，按标题解析 id）。两者都无则跳过。解析失败返回空列表（容错）。</summary>
    private static List<KnowledgeLink> ParseLinks(string? linksJson, IReadOnlyList<NoteDto> all)
    {
        var result = new List<KnowledgeLink>();
        if (string.IsNullOrWhiteSpace(linksJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(linksJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var role = el.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";

                Guid? targetId = null;
                string targetTitle = "";

                // 优先 targetId：真实节点 id（权威，改名不断链），按 id 反查标题
                if (el.TryGetProperty("targetId", out var tidEl) && Guid.TryParse(tidEl.GetString(), out var parsedId))
                {
                    targetId = parsedId;
                    var node = all.FirstOrDefault(n => Guid.TryParse(n.Id, out var nid) && nid == parsedId);
                    targetTitle = node?.Title ?? "";
                }
                // 回退 target：标题，按标题解析 id（兼容旧用法/手写）
                else if (el.TryGetProperty("target", out var tEl))
                {
                    targetTitle = tEl.GetString() ?? "";
                    var node = all.FirstOrDefault(n => string.Equals(n.Title, targetTitle, StringComparison.OrdinalIgnoreCase));
                    targetId = node is not null && Guid.TryParse(node.Id, out var nid) ? nid : null;
                }

                if (targetId is null && string.IsNullOrWhiteSpace(targetTitle)) continue;
                result.Add(new KnowledgeLink
                {
                    Role = role,
                    Target = targetTitle,
                    TargetId = targetId,
                });
            }
        }
        catch { /* 容错：links JSON 非法 → 返回空 links，仍创建知识对象 */ }
        return result;
    }

    /// <summary>组装：id + kind + name + 正文 + 各 link 指向子笔记的内容（含 target 真实节点 id）。
    /// <para><b>防上下文溢出</b>：link 目标笔记可能含完整字段表/SQL/示例，多目标内联会撑爆 LLM 上下文窗口
    /// （曾触发 GLM <c>ResponseEnded</c> 提前终止 → "The response ended prematurely"）。故每目标内联有上限、累计有上限，
    /// 超出截断头部 + 标记总数并告知 agent 如何取完整内容——同 <c>GrepTool</c>/<c>run_in_terminal</c> 的 Truncated 范式。
    /// 与文档版 <c>DocumentKnowledgeTool.Format</c> 同构（数据源=笔记内存 Content vs .ok 文件原文）。</para></summary>
    private const int MaxTargetInlineChars = 8000;       // 单个目标笔记内容内联上限
    private const int MaxLinkedInlineTotalChars = 20000; // 所有 link 内容内联累计上限（防多 link 累积撑爆）

    private async Task<string> FormatAsync(KnowledgeObjectDefinition def, string id, string title, IReadOnlyList<NoteDto> all, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"id: {id}");
        sb.AppendLine($"kind: {def.Kind}");
        sb.AppendLine($"name: {title}");
        if (!string.IsNullOrWhiteSpace(def.Body))
            sb.AppendLine().AppendLine(def.Body);

        if (def.Links.Count > 0)
        {
            sb.AppendLine().AppendLine("links:");
            var linkedTotal = 0; // 已内联的目标内容累计字符
            foreach (var l in def.Links)
            {
                // 权威按 TargetId 匹配；旧数据无 id 则回退到标题
                var target = l.TargetId.HasValue
                    ? all.FirstOrDefault(n => Guid.TryParse(n.Id, out var nid) && nid == l.TargetId.Value)
                    : null;
                target ??= all.FirstOrDefault(n => string.Equals(n.Title, l.Target, StringComparison.OrdinalIgnoreCase));
                // 输出 target 真实节点 id（数据现成：target 已查到），让 agent 能用权威 id 引用子笔记
                sb.AppendLine(target is not null
                    ? $"[{l.Role}] {l.Target} (id:{target.Id}):"
                    : $"[{l.Role}] {l.Target}:");

                if (linkedTotal >= MaxLinkedInlineTotalChars)
                {
                    // 累计已超上限：不再内联后续 link 内容，仅列名 + 提示（防多 link 累积撑爆）。
                    sb.AppendLine("  （引用过多，已停止展开后续目标；完整内容请单独 ReadKnowledgeObject 对应名称）");
                    continue;
                }

                if (target is null)
                {
                    sb.AppendLine("  （找不到笔记）");
                }
                else
                {
                    var content = target.Content ?? "(空)";
                    if (content.Length > MaxTargetInlineChars)
                    {
                        // 单个目标过大：截断头部（字段表/schema 信息密度高从头开始）+ 标记总数。
                        var truncated = content[..MaxTargetInlineChars]
                            + $"\n…（已截断，原文共 {content.Length} 字符；如需完整内容请单独 ReadKnowledgeObject \"{l.Target}\"）";
                        sb.AppendLine(Indent(truncated, "  "));
                        linkedTotal += MaxTargetInlineChars;
                    }
                    else
                    {
                        sb.AppendLine(Indent(content, "  "));
                        linkedTotal += content.Length;
                    }
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Indent(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return prefix;
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        return string.Join('\n', lines.Select(l => prefix + l));
    }
}
