using System.Globalization;

namespace Acme.Notes.AgentSource;

/// <summary>Agent 记忆模式。</summary>
public enum AgentMemoryMode
{
    SlidingWindow = 0,
    Full = 1,
    Off = 2,
}

/// <summary>Agent 源字段（解析结果）。</summary>
public sealed class AgentSourceFields
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    public List<string> Tools { get; set; } = new();
    public AgentMemoryMode Memory { get; set; } = AgentMemoryMode.SlidingWindow;
    public int MaxHistory { get; set; } = 20;
    /// <summary>模型标识（宿主解析，如 LLM 配置 Id；空=默认）。</summary>
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
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
                case "tools":
                    var tools = new List<string>();
                    if (value.StartsWith('[') && value.EndsWith(']'))
                    {
                        // flow style: [a, b, c]
                        var inner = value.Substring(1, value.Length - 2);
                        foreach (var t in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var tn = t.Trim().Trim('\'', '"');
                            if (!string.IsNullOrWhiteSpace(tn)) tools.Add(tn);
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
                                if (!string.IsNullOrWhiteSpace(tn)) tools.Add(tn);
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
                    result.Tools = tools;
                    break;
            }
            i++;
        }
        return true;
    }

    /// <summary>生成空白 Agent 源模板。</summary>
    public static string Template(string name = "新 Agent")
    {
        return $@"---
name: {name}
description: 由文本源创建
category: 普通对话
tools: []
memory: SlidingWindow
max_history: 20
model:
---
（在此写系统提示词）";
    }

    /// <summary>反向序列化：把字段组装成源文本。</summary>
    public static string Serialize(string name, string description, string category,
        IEnumerable<string> tools, AgentMemoryMode memory, int maxHistory, string model, string prompt)
    {
        var toolList = tools?.ToList() ?? new List<string>();
        var toolsLine = toolList.Count == 0
            ? "tools: []"
            : "tools:\n" + string.Join("\n", toolList.Select(t => $"- {t}"));

        return $@"---
name: {name}
description: {description}
category: {category}
{toolsLine}
memory: {memory}
max_history: {maxHistory}
model: {model ?? string.Empty}
---
{prompt}";
    }
}
