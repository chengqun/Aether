using Acme.Notes.Domain.Knowledge;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Knowledge;

/// <summary>
/// <see cref="IKnowledgeSourceParser"/> 的 YamlDotNet 实现。
/// 用内部 DTO 承载 YAML（Kind 以字符串进出，未识别值回退 <see cref="KnowledgeObjectKind.Unknown"/>），
/// 再与 Domain 的 <see cref="KnowledgeObjectDefinition"/>（枚举）互转。
/// YAML 键采用 camelCase（与默认模板一致）。
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

        try
        {
            var yaml = _deserializer.Deserialize<KnowledgeObjectYaml>(source);
            if (yaml is null)
            {
                error = "解析结果为空";
                return false;
            }

            var def = new KnowledgeObjectDefinition
            {
                Name = yaml.Name ?? string.Empty,
                DisplayName = yaml.DisplayName ?? string.Empty,
                Kind = ParseKind(yaml.Kind),
                Meaning = yaml.Meaning ?? string.Empty,
                Properties = yaml.Properties ?? new(),
                Relations = yaml.Relations ?? new(),
                DataBinding = yaml.DataBinding,
                Behavior = yaml.Behavior ?? new(),
                History = yaml.History ?? new(),
            };

            if (string.IsNullOrWhiteSpace(def.Name))
            {
                error = "缺少 name 字段";
                return false;
            }

            definition = def;
            return true;
        }
        catch (Exception ex)
        {
            error = "YAML 解析失败：" + ex.Message;
            return false;
        }
    }

    public string Serialize(KnowledgeObjectDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var yaml = new KnowledgeObjectYaml
        {
            Name = definition.Name,
            DisplayName = definition.DisplayName,
            Kind = definition.Kind.ToString(),
            Meaning = definition.Meaning,
            Properties = definition.Properties,
            Relations = definition.Relations,
            DataBinding = definition.DataBinding,
            Behavior = definition.Behavior,
            History = definition.History,
        };

        return "# KnowledgeObject 定义\n" + _serializer.Serialize(yaml);
    }

    private static KnowledgeObjectKind ParseKind(string? raw)
        => Enum.TryParse<KnowledgeObjectKind>(raw, ignoreCase: true, out var k)
            ? k
            : KnowledgeObjectKind.Unknown;

    /// <summary>YAML 承载 DTO：与 <see cref="KnowledgeObjectDefinition"/> 同构，但 Kind 为字符串以容忍未识别值。</summary>
    private sealed class KnowledgeObjectYaml
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Meaning { get; set; } = string.Empty;
        public List<KnowledgeProperty> Properties { get; set; } = new();
        public List<KnowledgeRelation> Relations { get; set; } = new();
        public KnowledgeDataBinding? DataBinding { get; set; }
        public List<KnowledgeBehavior> Behavior { get; set; } = new();
        public List<string> History { get; set; } = new();
    }
}
