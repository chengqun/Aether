using Acme.Notes.Domain.Knowledge;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Knowledge;

/// <summary>
/// 知识库规则清单装载器：读知识库根的 <c>knowledge-manifest.yaml</c>（词表/边方向矩阵/按 kind 必填
/// front-matter 字段），供 <see cref="KnowledgeGraphValidator"/> 与 <c>validate_knowledge</c> 工具体检。
/// <para>清单是<b>语料自治的数据文件</b>（每知识库一份、可编辑、随语料进 git）；无清单 = 合法
/// （校验退化为仅断链检查）。schema（camelCase）：</para>
/// <code>
/// kinds: [业务场景, 分析方法, …]
/// edges:
///   uses:        { from: [业务场景, 分析方法], to: ["*"] }
///   derived_from: { from: [已知认知], to: [企业资产, 业务场景产物] }
/// requiredFields:
///   已知认知: [subject, claim, source, confidence, first_seen, last_updated, status]
/// </code>
/// </summary>
public static class KnowledgeManifestLoader
{
    /// <summary>清单文件名（知识库根/labs knowledge/ 子目录下）。</summary>
    public const string FileName = "knowledge-manifest.yaml";

    /// <summary>装载清单。文件不存在 → true + manifest=null（合法，仅断链检查）；解析失败 → false + error。</summary>
    public static bool TryLoad(string knowledgeRoot, out KnowledgeManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            var file = Path.Combine(knowledgeRoot, FileName);
            if (!File.Exists(file)) return true;

            var yaml = File.ReadAllText(file);
            var dto = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ManifestDto>(yaml);
            if (dto is null) return true;

            IReadOnlyDictionary<string, KnowledgeEdgeRule>? edges = dto.Edges is null ? null
                : dto.Edges.ToDictionary(
                    kv => kv.Key,
                    kv => (KnowledgeEdgeRule)new KnowledgeEdgeRule(kv.Value?.From, kv.Value?.To));
            IReadOnlyDictionary<string, IReadOnlyList<string>>? required = dto.RequiredFields is null ? null
                : dto.RequiredFields.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)(kv.Value ?? new List<string>()));

            manifest = new KnowledgeManifest(dto.Kinds, edges, required);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{FileName} 解析失败: {ex.Message}";
            return false;
        }
    }

    private sealed class ManifestDto
    {
        public List<string>? Kinds { get; set; }
        public Dictionary<string, EdgeDto>? Edges { get; set; }
        public Dictionary<string, List<string>>? RequiredFields { get; set; }
    }

    private sealed class EdgeDto
    {
        public List<string>? From { get; set; }
        public List<string>? To { get; set; }
    }
}
