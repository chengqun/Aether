using System.Text.Encodings.Web;
using System.Text.Json;
using Acme.Notes.Domain.Agents;

namespace Acme.Notes.Infrastructure.Agents;

/// <summary>
/// <see cref="IWorkflowSourceParser"/> 实现：JSON 序列化/反序列化，直接操作
/// <see cref="CustomWorkflowDefinition"/>（单模型）。workflow 结构嵌套，比 agent 的 YAML 更适合 JSON。无第三方依赖。
/// </summary>
public sealed class WorkflowSourceParser : IWorkflowSourceParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Serialize(CustomWorkflowDefinition definition)
        => JsonSerializer.Serialize(definition ?? throw new ArgumentNullException(nameof(definition)), _options);

    public bool TryParse(string source, out CustomWorkflowDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "源文本为空";
            return false;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<CustomWorkflowDefinition>(source, _options);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
            {
                error = "缺少 name 或格式无效";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.Category))
                parsed.Category = "工作流";
            definition = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
