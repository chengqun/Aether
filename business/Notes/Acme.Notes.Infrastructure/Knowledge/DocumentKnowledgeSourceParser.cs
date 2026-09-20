using Acme.Notes.Domain.Knowledge;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Knowledge;

/// <summary>
/// <see cref="IDocumentKnowledgeSourceParser"/> 实现：文档版（<c>.md</c> 文件）知识对象源码解析。
/// 镜像 <see cref="KnowledgeSourceParser"/> 的 front-matter 拆分逻辑，但 links DTO 把 <c>targetId</c>/<c>target</c>
/// 都当**字符串**读——文档版 <c>targetId</c> 是指向项目内另一个 <c>.md</c> 文件的**相对路径**
/// （如 <c>Hive表/fin_dw.dwd_xxx</c>），不是 note 节点 Guid。复用 <see cref="KnowledgeObjectDefinition"/>
/// 作为内存模型：相对路径存入 <see cref="KnowledgeLink.Target"/>，<see cref="KnowledgeLink.TargetId"/> 留 <c>null</c>。
/// 序列化把路径写回 <c>targetId</c> 字段，保持用户既有 <c>.md</c> 文件格式（round-trip 稳定）。
/// </summary>
public sealed class DocumentKnowledgeSourceParser : IDocumentKnowledgeSourceParser
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
                    var head = _deserializer.Deserialize<DocumentKnowledgeHead>(yaml);
                    if (head is not null)
                    {
                        kind = head.Kind ?? string.Empty;
                        links = (head.Links ?? new()).Select(ToDomainLink).ToList();
                    }
                    // 未知键透传：kind/links 之外的顶层键（如 Intelligence 生命周期字段）原样保留——
                    // 修复前 typed DTO + IgnoreUnmatchedProperties 会在表单/agent 编辑回写时静默剥掉它们。
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
            // 无 front-matter：全文为正文（容错旧文件）
            body = text;
        }

        definition = new KnowledgeObjectDefinition { Kind = kind, Links = links, Body = body, ExtraFrontMatter = extras };
        return true;
    }

    public string Serialize(KnowledgeObjectDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var head = new DocumentKnowledgeHead
        {
            Kind = definition.Kind,
            Links = (definition.Links ?? new()).Select(ToDtoLink).ToList(),
        };
        var yaml = _serializer.Serialize(head).TrimEnd();
        // 未知键透传：额外键原样写回（kind/links 之后，与语料既有文件顺序一致）。
        if (definition.ExtraFrontMatter is { Count: > 0 } extras)
        {
            var extrasYaml = _serializer.Serialize(extras).TrimEnd();
            yaml = yaml + "\n" + extrasYaml;
        }
        return $"---\n{yaml}\n---\n{definition.Body ?? string.Empty}";
    }

    /// <summary>额外键 YAML 文本 → 有序字典（YamlDotNet 无类型反序列化保插入序）。空白/非法返 null（不阻断）。</summary>
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
            // 额外键 YAML 非法：kind/links 已解析成功——放弃透传（不阻断主体），序列化时不写回该部分。
            return null;
        }
    }

    /// <summary>DTO→Domain：相对路径优先取 targetId（用户既有格式），回退 target，存入 <see cref="KnowledgeLink.Target"/>；TargetId 留 null。</summary>
    private static KnowledgeLink ToDomainLink(DocumentKnowledgeLinkDto l) => new()
    {
        Role = l.Role ?? string.Empty,
        Target = !string.IsNullOrWhiteSpace(l.TargetId) ? l.TargetId!
              : !string.IsNullOrWhiteSpace(l.Target) ? l.Target!
              : string.Empty,
        Expand = l.Expand ?? false,
    };

    /// <summary>Domain→DTO：路径写回 targetId 字段（保持用户既有 .md 格式；target 字段留空避免冗余）。
    /// expand 仅在 true 时写出（OmitNull + bool? → 默认 false 省略，保持文件干净）。</summary>
    private static DocumentKnowledgeLinkDto ToDtoLink(KnowledgeLink l) => new()
    {
        Role = l.Role ?? string.Empty,
        TargetId = l.Target ?? string.Empty,
        Expand = l.Expand ? true : null,
    };

    /// <summary>front-matter 承载 DTO（kind + links，links 的 targetId/target 均为字符串）。</summary>
    private sealed class DocumentKnowledgeHead
    {
        public string Kind { get; set; } = string.Empty;
        public List<DocumentKnowledgeLinkDto> Links { get; set; } = new();
    }

    /// <summary>文档版 link DTO：targetId=相对路径（string），非 note 版的 Guid?。expand 可空（null=省略，true=内联）。</summary>
    private sealed class DocumentKnowledgeLinkDto
    {
        public string Role { get; set; } = string.Empty;
        public string? TargetId { get; set; }
        public string? Target { get; set; }
        public bool? Expand { get; set; }
    }
}
