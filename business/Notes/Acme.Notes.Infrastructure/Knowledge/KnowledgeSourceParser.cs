using Acme.Notes.Domain.Knowledge;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Knowledge;

/// <summary>
/// <see cref="IKnowledgeSourceParser"/> 实现：front-matter（kind + links）+ markdown 正文，
/// 与 AgentSourceParser 同构。front-matter 用 YamlDotNet 解析，正文为余下文本。
/// </summary>
public sealed class KnowledgeSourceParser : IKnowledgeSourceParser
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public bool TryParse(string source, out KnowledgeObjectDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = "源文本为空";
            return false;
        }

        var text = source.TrimStart();
        string body;
        string kind = string.Empty;
        List<KnowledgeLink> links = new();
        Dictionary<string, object?>? extras = null;

        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var rest = text.Substring(3);
            var idx = rest.IndexOf("\n---", StringComparison.Ordinal);
            if (idx < 0)
            {
                // 有起始无结束：当作无 front-matter，全文为正文（容错）
                body = text;
            }
            else
            {
                var yaml = rest.Substring(0, idx);
                body = rest.Substring(idx + 4).TrimStart('\r', '\n');
                try
                {
                    var head = _deserializer.Deserialize<KnowledgeHead>(yaml);
                    if (head is not null)
                    {
                        kind = head.Kind ?? string.Empty;
                        links = head.Links ?? new();
                    }
                    // 未知键透传（与 DocumentKnowledgeSourceParser 同构）。
                    extras = ParseExtras(FrontMatterExtras.ExtractExtrasText(yaml));
                }
                catch (Exception ex)
                {
                    error = "front-matter 解析失败：" + ex.Message;
                    return false;
                }
            }
        }
        else
        {
            // 无 front-matter：全文为正文（容错旧笔记）
            body = text;
        }

        definition = new KnowledgeObjectDefinition { Kind = kind, Links = links, Body = body, ExtraFrontMatter = extras };
        return true;
    }

    public string Serialize(KnowledgeObjectDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var head = new KnowledgeHead { Kind = definition.Kind, Links = definition.Links };
        var yaml = _serializer.Serialize(head).TrimEnd();
        // 未知键透传（与 DocumentKnowledgeSourceParser 同构）：额外键原样写回。
        if (definition.ExtraFrontMatter is { Count: > 0 } extras)
        {
            var extrasYaml = _serializer.Serialize(extras).TrimEnd();
            yaml = yaml + "\n" + extrasYaml;
        }
        return $"---\n{yaml}\n---\n{definition.Body ?? string.Empty}";
    }

    /// <summary>额外键 YAML 文本 → 有序字典。空白/非法返 null（不阻断）。</summary>
    private static Dictionary<string, object?>? ParseExtras(string extrasText)
    {
        if (string.IsNullOrWhiteSpace(extrasText)) return null;
        try
        {
            var dict = _deserializer.Deserialize<Dictionary<string, object?>>(extrasText);
            return dict is { Count: > 0 } ? dict : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>front-matter 承载 DTO（kind + links）。</summary>
    private sealed class KnowledgeHead
    {
        public string Kind { get; set; } = string.Empty;
        public List<KnowledgeLink> Links { get; set; } = new();
    }
}
