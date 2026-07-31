using System.ComponentModel;
using System.Text;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Notes.AI.Tools;

/// <summary>
/// 让 Agent 读取知识对象（KnowledgeObject）的工具（设计文档"AI As Consumer"最小形态）。
/// Agent 调用这些工具取出领域知识模型，按其属性/关系/行为推理执行。
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
        public string? MeaningPreview { get; set; }
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
        /// <summary>结构化文本描述（属性/关系/数据绑定/行为），供 Agent 推理。</summary>
        public string? Definition { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出所有可用的领域知识对象（KnowledgeObject）。返回名称、类型与含义预览。")]
    public async Task<ListKnowledgeOutput> ListKnowledgeObjects(CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _notes.GetAllAsync(cancellationToken);
            var result = new List<KnowledgeSummary>();
            foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Knowledge))
            {
                if (!_parser.TryParse(note.Content, out var def, out _) || def is null) continue;
                result.Add(new KnowledgeSummary
                {
                    Name = def.Name,
                    Kind = def.Kind.ToString(),
                    MeaningPreview = string.IsNullOrWhiteSpace(def.Meaning) ? null : Truncate(def.Meaning, 80),
                });
            }
            return new ListKnowledgeOutput { KnowledgeObjects = result };
        }
        catch (Exception ex)
        {
            return new ListKnowledgeOutput { Error = ex.Message };
        }
    }

    [Description("按名称读取一个领域知识对象（KnowledgeObject）的完整定义：类型、含义、属性、关系、数据绑定、行为。先调用此工具获取知识，再按其模型执行任务。")]
    public async Task<ReadKnowledgeOutput> ReadKnowledgeObject(
        [Description("知识对象名称（name）")] string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ReadKnowledgeOutput { Error = "名称不能为空" };

            var all = await _notes.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.NodeType == NoteNodeType.Knowledge
                && _parser.TryParse(n.Content, out var d, out _)
                && string.Equals(d!.Name, name, StringComparison.OrdinalIgnoreCase));
            if (note is null)
                return new ReadKnowledgeOutput { Error = $"找不到知识对象: {name}" };

            if (!_parser.TryParse(note.Content, out var def, out var err) || def is null)
                return new ReadKnowledgeOutput { Error = $"解析失败: {err}" };

            return new ReadKnowledgeOutput
            {
                Name = def.Name,
                Kind = def.Kind.ToString(),
                Definition = Format(def),
            };
        }
        catch (Exception ex)
        {
            return new ReadKnowledgeOutput { Error = ex.Message };
        }
    }

    /// <summary>把定义格式化为 Agent 易读的结构化文本。</summary>
    private static string Format(KnowledgeObjectDefinition d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kind: {d.Kind}");
        sb.AppendLine($"name: {d.Name}");
        if (!string.IsNullOrWhiteSpace(d.DisplayName)) sb.AppendLine($"display_name: {d.DisplayName}");
        if (!string.IsNullOrWhiteSpace(d.Meaning)) sb.AppendLine($"meaning: {d.Meaning}");

        if (d.Properties.Count > 0)
        {
            sb.AppendLine("properties:");
            foreach (var p in d.Properties)
                sb.AppendLine($"  - {p.Name} ({p.Type}): {p.Description}");
        }

        if (d.Relations.Count > 0)
        {
            sb.AppendLine("relations:");
            foreach (var r in d.Relations)
                sb.AppendLine($"  - {r.Target}  --{r.Type}-->  {r.Description}");
        }

        if (d.DataBinding is not null
            && (!string.IsNullOrWhiteSpace(d.DataBinding.Source) || !string.IsNullOrWhiteSpace(d.DataBinding.Table)))
            sb.AppendLine($"data_binding: {d.DataBinding.Source}/{d.DataBinding.Table}");

        if (d.Behavior.Count > 0)
        {
            sb.AppendLine("behavior:");
            foreach (var b in d.Behavior)
                sb.AppendLine($"  - {b.Name} ({b.Type}): {b.Expression}");
        }

        if (d.History.Count > 0)
        {
            sb.AppendLine("history:");
            foreach (var h in d.History)
                sb.AppendLine($"  - {h}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";
}
