using System.Globalization;
using Acme.Notes.Domain.Agents;

namespace Acme.Notes.AgentSource;

/// <summary>
/// 轻量 Agent 源文本解析器（无第三方依赖）。
/// 格式：YAML-like front-matter + Markdown 正文（正文=系统提示词）。
/// 直接 (de)serialize <see cref="CustomAgentDefinition"/>（单模型，向 Knowledge 看齐）。
/// </summary>
public static class AgentSourceParser
{
    /// <summary>尝试解析 agent 源文本。</summary>
    public static bool TryParse(string source, out CustomAgentDefinition? definition, out string error)
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
        var prompt = rest.Substring(idx + 4).TrimStart('\r', '\n');

        var result = new CustomAgentDefinition { SystemPrompt = prompt };
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

        if (string.IsNullOrWhiteSpace(result.Category))
            result.Category = "普通对话";

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
            if (colonIdx < 0)
            {
                i++;
                continue;
            }

            var key = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant().Replace("_", "");
            var value = trimmed.Substring(colonIdx + 1).Trim();

            switch (key)
            {
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
                    if (Enum.TryParse<AgentMemoryMode>(value, true, out var mm))
                        result.Memory = mm;
                    break;
                case "maxhistory":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mh))
                        result.MaxHistory = mh;
                    break;
                case "model":
                    result.ModelId = value;
                    break;
                case "source":
                    result.Source = value;
                    break;
                case "marketplaceid":
                    if (Guid.TryParse(value, out var mid)) result.MarketplaceId = mid;
                    break;
                case "marketplacesyncedat":
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var msync))
                        result.MarketplaceSyncedAt = msync;
                    break;
                case "displayname":
                    result.DisplayName = value;
                    break;
                case "topp":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tp))
                        result.TopP = tp;
                    break;
                case "maxoutputtokens":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mot))
                        result.MaxOutputTokens = mot;
                    break;
                case "responseformat":
                    if (Enum.TryParse<AgentResponseFormat>(value, true, out var rf))
                        result.ResponseFormat = rf;
                    break;
                case "enablewebsearch":
                    if (bool.TryParse(value, out var ws)) result.EnableWebSearch = ws;
                    break;
                case "enabletodoprovider":
                    if (bool.TryParse(value, out var td)) result.EnableTodoProvider = td;
                    break;
                case "enableagentmodeprovider":
                    if (bool.TryParse(value, out var am)) result.EnableAgentModeProvider = am;
                    break;
                case "enablefilememory":
                    if (bool.TryParse(value, out var fm)) result.EnableFileMemory = fm;
                    break;
                case "enableagentskills":
                    if (bool.TryParse(value, out var sk)) result.EnableAgentSkills = sk;
                    break;
                case "enableopentelemetry":
                    if (bool.TryParse(value, out var ot)) result.EnableOpenTelemetry = ot;
                    break;
                case "tools":
                    result.Tools = ParseListValue(value, lines, ref i);
                    break;
                case "skills":
                    result.Skills = ParseListValue(value, lines, ref i);
                    break;
            }
            i++;
        }
        return true;
    }

    /// <summary>解析 flow（[a, b]）或 block（下一行起 - a / - b）形式的字符串列表，block 形式会推进 i。</summary>
    private static List<string> ParseListValue(string value, string[] lines, ref int i)
    {
        var list = new List<string>();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            // flow style: [a, b, c]
            var inner = value.Substring(1, value.Length - 2);
            foreach (var t in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tn = t.Trim().Trim('\'', '"');
                if (!string.IsNullOrWhiteSpace(tn)) list.Add(tn);
            }
        }
        else if (string.IsNullOrWhiteSpace(value))
        {
            // block style: 下一行以 - 开头
            while (i + 1 < lines.Length)
            {
                var next = lines[i + 1].Trim();
                if (next.StartsWith("- "))
                {
                    var tn = next.Substring(2).Trim().Trim('\'', '"');
                    if (!string.IsNullOrWhiteSpace(tn)) list.Add(tn);
                    i++;
                }
                else if (string.IsNullOrWhiteSpace(next))
                {
                    i++;
                }
                else
                {
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>生成空白 Agent 源模板。</summary>
    public static string Template(string name = "新 Agent")
    {
        return $@"---
name: {name}
description: 由文本源创建
category: 普通对话
tools: []
skills: []
memory: SlidingWindow
max_history: 20
model:
response_format: Default
---
（在此写系统提示词）";
    }

    /// <summary>反向序列化：把定义组装成源文本。</summary>
    public static string Serialize(CustomAgentDefinition f)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));

        var toolList = f.Tools?.ToList() ?? new List<string>();
        var toolsLine = toolList.Count == 0
            ? "tools: []"
            : "tools:\n" + string.Join("\n", toolList.Select(t => $"- {t}"));

        var skillList = f.Skills?.ToList() ?? new List<string>();
        var skillsLine = skillList.Count == 0
            ? "skills: []"
            : "skills:\n" + string.Join("\n", skillList.Select(s => $"- {s}"));

        return $@"---
name: {f.Name}
description: {f.Description}
category: {f.Category}
{toolsLine}
{skillsLine}
memory: {f.Memory}
max_history: {f.MaxHistory}
model: {f.ModelId ?? string.Empty}
top_p: {(f.TopP.HasValue ? f.TopP.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty)}
max_output_tokens: {(f.MaxOutputTokens.HasValue ? f.MaxOutputTokens.Value.ToString(CultureInfo.InvariantCulture) : string.Empty)}
response_format: {f.ResponseFormat}
enable_web_search: {f.EnableWebSearch.ToString().ToLowerInvariant()}
enable_todo_provider: {f.EnableTodoProvider.ToString().ToLowerInvariant()}
enable_agent_mode_provider: {f.EnableAgentModeProvider.ToString().ToLowerInvariant()}
enable_file_memory: {f.EnableFileMemory.ToString().ToLowerInvariant()}
enable_agent_skills: {f.EnableAgentSkills.ToString().ToLowerInvariant()}
enable_open_telemetry: {f.EnableOpenTelemetry.ToString().ToLowerInvariant()}
source: {f.Source ?? string.Empty}
marketplace_id: {(f.MarketplaceId?.ToString() ?? string.Empty)}
marketplace_synced_at: {(f.MarketplaceSyncedAt.HasValue ? f.MarketplaceSyncedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : string.Empty)}
display_name: {f.DisplayName ?? string.Empty}
---
{f.SystemPrompt}";
    }
}
