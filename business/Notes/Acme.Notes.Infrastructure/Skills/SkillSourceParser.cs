using Acme.Notes.Domain.Skills;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Skills;

/// <summary>
/// <see cref="ISkillSourceParser"/> 实现：SKILL.md = YAML front-matter（name/description/category）+ markdown 正文。
/// 镜像 <see cref="Acme.Notes.Infrastructure.Knowledge.DocumentKnowledgeSourceParser"/> 的 front-matter 拆分/序列化逻辑，
/// 去掉 links。容错无 front-matter（全文当正文）、无结束 fence（全文当正文）。往返稳定。
/// </summary>
public sealed class SkillSourceParser : ISkillSourceParser
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public bool TryParse(string source, out SkillSourceDefinition? definition, out string error)
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
        string name = string.Empty;
        string description = string.Empty;
        string category = string.Empty;

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
                    var head = _deserializer.Deserialize<SkillHead>(yaml);
                    if (head is not null)
                    {
                        name = head.Name ?? string.Empty;
                        description = head.Description ?? string.Empty;
                        category = head.Category ?? string.Empty;
                    }
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

        definition = new SkillSourceDefinition { Name = name, Description = description, Category = category, Body = body };
        return true;
    }

    public string Serialize(SkillSourceDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var head = new SkillHead
        {
            Name = definition.Name,
            Description = definition.Description,
            Category = definition.Category,
        };
        var yaml = _serializer.Serialize(head).TrimEnd();
        return $"---\n{yaml}\n---\n{definition.Body ?? string.Empty}";
    }

    /// <summary>front-matter 承载 DTO（name/description/category）。</summary>
    private sealed class SkillHead
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }
}
