using System.Globalization;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;

namespace Acme.Notes.Infrastructure.Agents;

/// <summary>
/// <see cref="ICodeSourceParser"/> 实现：代码规则笔记 = YAML front-matter（元数据）+ 原始 C# 正文。
/// 镜像 <see cref="AgentSourceParser"/> 的 front-matter 拆分（C# 正文不可 YAML 解析，故用 front-matter 分隔，
/// 而非 <see cref="RuleSourceParser"/> 的单 YAML 文档）。元数据字段集与规则编辑器一致（无模型/采样/skill——
/// 代码规则无 LLM）。body 进 <see cref="CustomAgentDefinition.SystemPrompt"/>，<c>Engine</c> 恒 "Rule"，
/// <c>NodeType</c> 恒 <see cref="NoteNodeType.CodeRule"/>。不编译——编译由平台 Roslyn 服务负责。
/// </summary>
public sealed class CodeSourceParser : ICodeSourceParser
{
    public bool TryParse(string source, out CustomAgentDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = "源文本为空";
            return false;
        }

        var text = source.TrimStart();
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            error = "缺少 front-matter 起始 ---";
            return false;
        }

        var rest = text.Substring(3);
        var idx = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (idx < 0)
        {
            error = "找不到 front-matter 结束 ---";
            return false;
        }

        var yamlLines = rest.Substring(0, idx).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var body = rest.Substring(idx + 4).TrimStart('\r', '\n');

        var result = new CustomAgentDefinition
        {
            SystemPrompt = body,
            Engine = "Rule",
            NodeType = NoteNodeType.CodeRule,
            Category = "代码规则",
            Memory = AgentMemoryMode.Off,
        };
        if (!ParseFrontMatter(yamlLines, result, out var parseError))
        {
            error = parseError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.Name))
        {
            error = "front-matter 缺少 name 字段";
            return false;
        }

        definition = result;
        return true;
    }

    private static bool ParseFrontMatter(string[] lines, CustomAgentDefinition result, out string error)
    {
        error = string.Empty;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                i++;
                continue;
            }

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) { i++; continue; }

            var key = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant().Replace("_", "");
            var value = trimmed.Substring(colonIdx + 1).Trim();

            switch (key)
            {
                case "id":
                    if (Guid.TryParse(value, out var gid)) result.Id = gid;
                    break;
                case "name":
                    result.Name = value;
                    break;
                case "description":
                    result.Description = value;
                    break;
                case "category":
                    result.Category = value;
                    break;
                case "memory":
                    if (Enum.TryParse<AgentMemoryMode>(value, true, out var mm)) result.Memory = mm;
                    break;
                case "maxhistory":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mh)) result.MaxHistory = mh;
                    break;
                case "model":
                    result.ModelId = value;
                    break;
                case "requiretoolapproval":
                    if (bool.TryParse(value, out var ra)) result.RequireToolApproval = ra;
                    break;
                case "tools":
                    result.Tools = ParseListValue(value, lines, ref i);
                    break;
                case "toolapprovals":
                    try
                    {
                        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(value);
                        if (map is not null) result.ToolApprovals = map;
                    }
                    catch { /* 工具审批覆盖反序列化失败：忽略 */ }
                    break;
            }
            i++;
        }
        return true;
    }

    /// <summary>解析 flow（[a, b]）或 block（下一行起 - a / - b）形式的字符串列表。</summary>
    private static List<string> ParseListValue(string value, string[] lines, ref int i)
    {
        var list = new List<string>();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var inner = value.Substring(1, value.Length - 2);
            foreach (var t in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tn = t.Trim().Trim('\'', '"');
                if (!string.IsNullOrWhiteSpace(tn)) list.Add(tn);
            }
        }
        else if (string.IsNullOrWhiteSpace(value))
        {
            while (i + 1 < lines.Length)
            {
                var next = lines[i + 1].Trim();
                if (next.StartsWith("- "))
                {
                    var tn = next.Substring(2).Trim().Trim('\'', '"');
                    if (!string.IsNullOrWhiteSpace(tn)) list.Add(tn);
                    i++;
                }
                else if (string.IsNullOrWhiteSpace(next)) { i++; }
                else break;
            }
        }
        return list;
    }

    public string Serialize(CustomAgentDefinition f)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));

        var toolList = f.Tools?.ToList() ?? new List<string>();
        var toolsLine = toolList.Count == 0
            ? "tools: []"
            : "tools:\n" + string.Join("\n", toolList.Select(t => $"- {t}"));

        var approvalsLine = (f.ToolApprovals is null || f.ToolApprovals.Count == 0)
            ? null
            : "tool_approvals: " + System.Text.Json.JsonSerializer.Serialize(f.ToolApprovals);

        return $@"---
id: {f.Id}
name: {f.Name}
description: {f.Description}
category: {f.Category}
{toolsLine}
{approvalsLine}
memory: {f.Memory}
max_history: {f.MaxHistory}
model: {f.ModelId ?? string.Empty}
require_tool_approval: {f.RequireToolApproval.ToString().ToLowerInvariant()}
---
{f.SystemPrompt}";
    }
}
