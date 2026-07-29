using System.Text.Encodings.Web;
using System.Text.Json;

namespace Acme.Notes.AgentSource;

/// <summary>workflow 转交边（持久化用，与 Core 的 WorkflowHandoff 平行，business 不引 Aether.AI.*）。</summary>
public sealed class WorkflowHandoffField
{
    public Guid FromAgentId { get; set; }
    public Guid ToAgentId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>workflow 源字段（JSON 内容，存于 NoteNodeType.Workflow 笔记的 Content）。</summary>
public sealed class WorkflowSourceFields
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "工作流";
    public Guid EntryAgentId { get; set; }
    public List<Guid> Participants { get; set; } = new();
    public List<WorkflowHandoffField> Handoffs { get; set; } = new();
    public string? HandoffInstructions { get; set; }
}

/// <summary>
/// workflow 源文本解析器：JSON 序列化/反序列化（workflow 结构嵌套，比 agent 的 YAML 更适合 JSON）。
/// 无第三方依赖。
/// </summary>
public static class WorkflowSourceParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(WorkflowSourceFields fields)
        => JsonSerializer.Serialize(fields ?? throw new ArgumentNullException(nameof(fields)), _options);

    public static bool TryParse(string source, out WorkflowSourceFields? fields, out string error)
    {
        fields = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "源文本为空";
            return false;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkflowSourceFields>(source, _options);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
            {
                error = "缺少 name 或格式无效";
                return false;
            }
            fields = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
