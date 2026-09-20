using System.Text;

namespace Acme.Notes.Infrastructure.Knowledge;

/// <summary>
/// front-matter 未知键透传助手（<see cref="KnowledgeSourceParser"/> 与
/// <see cref="DocumentKnowledgeSourceParser"/> 共用）。typed DTO 只建模 kind/links——语料自有元数据
/// （如 Intelligence 生命周期字段 subject/claim/…）会被 <c>IgnoreUnmatchedProperties</c> 静默丢弃。
/// 修复：行级拆分 front-matter，把已知键（kind/links）之外的顶层键块原样提取（保序、保留注释），
/// 经 <see cref="KnowledgeObjectDefinition.ExtraFrontMatter"/> 装载，序列化时原样写回。
/// </summary>
internal static class FrontMatterExtras
{
    /// <summary>从 front-matter YAML 文本提取已知键（kind/links）之外的顶层键块（原样行，保序）。
    /// 顶层键 = 行首非空白且含 ':' 的行；其缩进续行/列表项归属该键。顶层注释行原样保留。</summary>
    public static string ExtractExtrasText(string yaml)
    {
        var sb = new StringBuilder();
        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? currentKey = null;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                // 空行：归属当前键（已知键的尾随空行丢弃，额外键之间的空行保留）
                if (currentKey is not null && !IsKnownKey(currentKey)) sb.AppendLine(line);
                continue;
            }
            if (line[0] == ' ' || line[0] == '-')
            {
                // 缩进续行 / 列表项：归属当前顶层键
                if (currentKey is not null && !IsKnownKey(currentKey)) sb.AppendLine(line);
                continue;
            }

            // 顶层行：解析键名（首个 ':' 前）
            var colon = line.IndexOf(':');
            currentKey = colon > 0 ? line[..colon].Trim() : null;
            if (currentKey is not null && !IsKnownKey(currentKey))
                sb.AppendLine(line);
            else if (currentKey is null)
                sb.AppendLine(line); // 顶层注释等非键行：原样保留（保序容错）
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static bool IsKnownKey(string key)
        => string.Equals(key, "kind", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "links", StringComparison.OrdinalIgnoreCase);
}
