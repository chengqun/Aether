using System.Globalization;

namespace Acme.Notes.AgentSource;

/// <summary>Agent 记忆模式。</summary>
public enum AgentMemoryMode
{
    SlidingWindow = 0,
    Full = 1,
    Off = 2,
}

/// <summary>Agent 响应格式（与平台 Core 的 AgentResponseFormat 平行，business 层不引用 Aether.AI.*）。</summary>
public enum AgentResponseFormat
{
    Default = 0,
    Text = 1,
    Json = 2,
}

/// <summary>Agent 源字段（解析结果）。</summary>
public sealed class AgentSourceFields
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    public List<string> Tools { get; set; } = new();
    /// <summary>该 agent 启用 skills 时可用的 skill 名列表（空=全部已注册 skill）。</summary>
    public List<string> Skills { get; set; } = new();
    public AgentMemoryMode Memory { get; set; } = AgentMemoryMode.SlidingWindow;
    public int MaxHistory { get; set; } = 20;
    /// <summary>模型标识（宿主解析，如 LLM 配置 Id；空=默认）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>来源："Local"|"Marketplace"。空=本地。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>市场云端 Id。</summary>
    public Guid? MarketplaceId { get; set; }

    /// <summary>上次同步市场的云端 LastModifiedAt。</summary>
    public DateTimeOffset? MarketplaceSyncedAt { get; set; }

    /// <summary>市场原名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    // —— 采样 / 输出参数（可空=沿用 provider 默认）。
    public double? TopP { get; set; }
    public int? MaxOutputTokens { get; set; }
    public AgentResponseFormat ResponseFormat { get; set; } = AgentResponseFormat.Default;

    // —— HarnessAgent 扩展能力开关（默认全 false = 全部禁用，保持轻量运行时）。
    public bool EnableWebSearch { get; set; }
    public bool EnableTodoProvider { get; set; }
    public bool EnableAgentModeProvider { get; set; }
    public bool EnableFileMemory { get; set; }
    public bool EnableAgentSkills { get; set; }
    public bool EnableOpenTelemetry { get; set; }
}

/// <summary>
/// 轻量 Agent 源文本解析器（无第三方依赖）。
/// 格式：YAML-like front-matter + Markdown 正文。
/// 支持简单 key: value 和 tools 列表（flow [a, b] 或 block - a）。
/// </summary>
public static class AgentSourceParser
{
    /// <summary>尝试解析 agent 源文本。</summary>
    public static bool TryParse(string source, out AgentSourceFields? fields, out string error)
    {
        fields = null;
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

        var result = new AgentSourceFields { Prompt = prompt };
        var parseResult = ParseFrontMatter(yamlLines, result, out var parseError);
        if (!parseResult)
        {
            error = parseError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.Name))
        {
            error = "front-matter 缺少 name 字段";
            return false;
        }

        fields = result;
        return true;
    }

    private static bool ParseFrontMatter(string[] lines, AgentSourceFields result, out string error)
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
                    result.Category = string.IsNullOrWhiteSpace(value) ? "普通对话" : value;
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
                    result.Model = value;
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

    /// <summary>反向序列化：把字段组装成源文本。</summary>
    public static string Serialize(AgentSourceFields f)
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
model: {f.Model ?? string.Empty}
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
{f.Prompt}";
    }
}
