using Acme.Notes.Domain.Knowledge;
using Acme.Notes.Infrastructure.Knowledge;

namespace Acme.Notes.Infrastructure.Tests;

/// <summary>
/// 知识对象图体检器测试（<see cref="KnowledgeGraphValidator"/> 纯函数 + <see cref="KnowledgeManifestLoader"/>）。
/// 四类检查：断链（error）/ kind 词表 / 边类型与方向矩阵 / 按 kind 必填 front-matter 字段。
/// </summary>
public class KnowledgeGraphValidatorTests
{
    private static KnowledgeObjectDefinition Def(string kind, params (string Role, string Target)[] links) => new()
    {
        Kind = kind,
        Links = links.Select(l => new KnowledgeLink { Role = l.Role, Target = l.Target }).ToList(),
    };

    private static KnowledgeManifest SampleManifest() => new(
        Kinds: new[] { "业务场景", "已知认知", "分析特征" },
        Edges: new Dictionary<string, KnowledgeEdgeRule>
        {
            ["uses"] = new KnowledgeEdgeRule(new[] { "业务场景" }, new[] { "*" }),
            ["derived_from"] = new KnowledgeEdgeRule(new[] { "已知认知", "分析特征" }, new[] { "分析特征" }),
        },
        RequiredFields: new Dictionary<string, IReadOnlyList<string>>
        {
            ["已知认知"] = new[] { "subject", "claim" },
        });

    /// <summary>目标解析 fake：目标名 → 项（找不到 null=断链）。</summary>
    private static KnowledgeItem? Resolve(
        IReadOnlyDictionary<string, KnowledgeItem> index, string target, string _)
        => index.TryGetValue(target, out var item) ? item : null;

    [Fact]
    public void Broken_link_reports_error()
    {
        var items = new List<KnowledgeItem> { new("场景/a", Def("业务场景", ("uses", "不存在的目标"))) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(),
            (t, s) => Resolve(new Dictionary<string, KnowledgeItem>(), t, s));

        var broken = Assert.Single(diagnostics);
        Assert.Equal("error", broken.Severity);
        Assert.Equal("断链", broken.Category);
        Assert.Contains("不存在的目标", broken.Message);
    }

    [Fact]
    public void Kind_vocabulary_warns_on_unknown_kind()
    {
        var items = new List<KnowledgeItem> { new("场景/a", Def("产物")) }; // 词表外（清单无「产物」）
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => null);

        var warning = Assert.Single(diagnostics);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal("kind", warning.Category);
        Assert.Contains("产物", warning.Message);
    }

    [Fact]
    public void Missing_kind_warns()
    {
        var items = new List<KnowledgeItem> { new("场景/a", Def("")) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => null);
        Assert.Contains(diagnostics, d => d.Category == "kind" && d.Message.Contains("缺少 kind"));
    }

    [Fact]
    public void Edge_from_direction_warns()
    {
        // uses 的 from 列表只有「业务场景」——从「已知认知」发出 uses → 越界。
        var items = new List<KnowledgeItem> { new("场景/a", Def("已知认知", ("uses", "特征/b"))) };
        var index = new Dictionary<string, KnowledgeItem> { ["特征/b"] = new("特征/b", Def("分析特征")) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => Resolve(index, t, s));

        Assert.Contains(diagnostics, d => d.Category == "边方向" && d.Message.Contains("uses") && d.Message.Contains("已知认知"));
    }

    [Fact]
    public void Edge_to_direction_warns()
    {
        // derived_from 的 to 列表只有「分析特征」——指向「企业资产」→ 越界。
        var items = new List<KnowledgeItem> { new("场景/a", Def("已知认知", ("derived_from", "资产/b"))) };
        var index = new Dictionary<string, KnowledgeItem> { ["资产/b"] = new("资产/b", Def("企业资产")) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => Resolve(index, t, s));

        Assert.Contains(diagnostics, d => d.Category == "边方向" && d.Message.Contains("derived_from") && d.Message.Contains("企业资产"));
    }

    [Fact]
    public void Unknown_role_warns()
    {
        var items = new List<KnowledgeItem> { new("场景/a", Def("业务场景", ("produces", "特征/b"))) };
        var index = new Dictionary<string, KnowledgeItem> { ["特征/b"] = new("特征/b", Def("分析特征")) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => Resolve(index, t, s));

        Assert.Contains(diagnostics, d => d.Category == "边类型" && d.Message.Contains("produces"));
    }

    [Fact]
    public void Required_fields_warn_on_missing()
    {
        // 已知认知 必填 subject/claim——全缺 → 2 条；补上 subject → 1 条。
        var noFields = new List<KnowledgeItem> { new("认知/a", Def("已知认知")) };
        var d1 = KnowledgeGraphValidator.Validate(noFields, SampleManifest(), (t, s) => null);
        Assert.Equal(2, d1.Count(d => d.Category == "必填字段"));

        var withSubject = new KnowledgeObjectDefinition
        {
            Kind = "已知认知",
            ExtraFrontMatter = new Dictionary<string, object?> { ["subject"] = "s" },
        };
        var oneField = new List<KnowledgeItem> { new("认知/a", withSubject) };
        var d2 = KnowledgeGraphValidator.Validate(oneField, SampleManifest(), (t, s) => null);
        var missing = Assert.Single(d2.Where(d => d.Category == "必填字段"));
        Assert.Contains("claim", missing.Message);
    }

    [Fact]
    public void No_manifest_checks_only_broken_links()
    {
        // 无清单：词表/方向/必填字段全跳过，只查断链。
        var items = new List<KnowledgeItem> { new("场景/a", Def("随便什么kind", ("词表外role", "目标"))) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, null, (t, s) => null);

        var broken = Assert.Single(diagnostics);
        Assert.Equal("断链", broken.Category);
    }

    [Fact]
    public void Compliant_corpus_produces_no_diagnostics()
    {
        // 合规链：业务场景 uses→已知认知（to=*）；已知认知 derived_from→分析特征（from/to 均在列）；
        // 已知认知带齐必填字段。
        var items = new List<KnowledgeItem>
        {
            new("场景/a", Def("业务场景", ("uses", "认知/c"))),
            new("认知/c", new KnowledgeObjectDefinition
            {
                Kind = "已知认知",
                Links = new List<KnowledgeLink> { new() { Role = "derived_from", Target = "特征/b" } },
                ExtraFrontMatter = new Dictionary<string, object?>
                {
                    ["subject"] = "s", ["claim"] = "c",
                },
            }),
            new("特征/b", Def("分析特征")),
        };
        var index = items.ToDictionary(i => i.Path, i => i);
        var diagnostics = KnowledgeGraphValidator.Validate(items, SampleManifest(), (t, s) => Resolve(index, t, s));

        Assert.Empty(diagnostics);
    }

    // —— KnowledgeManifestLoader ——

    [Fact]
    public void ManifestLoader_missing_file_returns_true_with_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-km-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ok = KnowledgeManifestLoader.TryLoad(dir, out var manifest, out var error);
            Assert.True(ok);
            Assert.Null(manifest);
            Assert.Null(error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ManifestLoader_parses_sample_yaml()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-km-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, KnowledgeManifestLoader.FileName), """
                kinds:
                  - 业务场景
                  - 已知认知
                edges:
                  uses:
                    from: [业务场景]
                    to: ["*"]
                  derived_from:
                    from: [已知认知]
                    to: [企业资产, 业务场景产物]
                requiredFields:
                  已知认知:
                    - subject
                    - claim
                """);
            var ok = KnowledgeManifestLoader.TryLoad(dir, out var manifest, out var error);
            Assert.True(ok, error);
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest!.Kinds!.Count);
            Assert.Contains("企业资产", manifest.Edges!["derived_from"].To!);
            Assert.Equal(2, manifest.RequiredFields!["已知认知"].Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ManifestLoader_broken_yaml_returns_false_with_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-km-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, KnowledgeManifestLoader.FileName), "kinds: [unclosed");
            var ok = KnowledgeManifestLoader.TryLoad(dir, out _, out var error);
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally { Directory.Delete(dir, true); }
    }
}
