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

    [Description("列出所有知识对象（KnowledgeObject）。返回名称、kind、正文预览。")]
    public async Task<ListKnowledgeOutput> ListKnowledgeObjects(CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _notes.GetAllAsync(cancellationToken);
            var result = new List<KnowledgeSummary>();
            foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Knowledge))
            {
                if (!_parser.TryParse(note.Content, out var def, out _)) continue;
                result.Add(new KnowledgeSummary
                {
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
                Name = note.Title,
                Kind = def.Kind,
                Definition = await FormatAsync(def, note.Title, all, cancellationToken),
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
        [Description("links（可选），JSON 数组，如 [{\"role\":\"uses\",\"target\":\"情报分析表\"}]，target 为目标笔记标题（按标题解析为 id）")] string? linksJson = null,
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

    /// <summary>解析 links JSON（[{role,target}]），按 target 标题查笔记 id。解析失败返回空列表（容错）。</summary>
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
                var target = el.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(target)) continue;
                var node = all.FirstOrDefault(n => string.Equals(n.Title, target, StringComparison.OrdinalIgnoreCase));
                result.Add(new KnowledgeLink
                {
                    Role = role,
                    Target = target,
                    TargetId = node is not null && Guid.TryParse(node.Id, out var nid) ? nid : null,
                });
            }
        }
        catch { /* 容错：links JSON 非法 → 返回空 links，仍创建知识对象 */ }
        return result;
    }

    /// <summary>组装：kind + name + 正文 + 各 link 指向子笔记的内容。</summary>
    private async Task<string> FormatAsync(KnowledgeObjectDefinition def, string title, IReadOnlyList<NoteDto> all, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kind: {def.Kind}");
        sb.AppendLine($"name: {title}");
        if (!string.IsNullOrWhiteSpace(def.Body))
            sb.AppendLine().AppendLine(def.Body);

        if (def.Links.Count > 0)
        {
            sb.AppendLine().AppendLine("links:");
            foreach (var l in def.Links)
            {
                // 权威按 TargetId 匹配；旧数据无 id 则回退到标题
                var target = l.TargetId.HasValue
                    ? all.FirstOrDefault(n => Guid.TryParse(n.Id, out var nid) && nid == l.TargetId.Value)
                    : null;
                target ??= all.FirstOrDefault(n => string.Equals(n.Title, l.Target, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"[{l.Role}] {l.Target}:");
                if (target is null)
                    sb.AppendLine("  （找不到笔记）");
                else
                    sb.AppendLine(Indent(target.Content ?? "(空)", "  "));
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
