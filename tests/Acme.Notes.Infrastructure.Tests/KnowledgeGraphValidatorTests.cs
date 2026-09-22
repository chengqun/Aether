using Acme.Notes.Domain.Knowledge;

namespace Acme.Notes.Infrastructure.Tests;

/// <summary>
/// 知识对象图体检器测试（<see cref="KnowledgeGraphValidator"/> 纯函数）。检查断链（error 级）——
/// 词表/边方向矩阵/按 kind 必填字段的 knowledge-manifest.yaml 机制已于 2026-09-22 移除
/// （软警告维护成本高于收益；语料规范改由各项目 README 散文承载，断链检查与清单无关、保留）。
/// </summary>
public class KnowledgeGraphValidatorTests
{
    private static KnowledgeObjectDefinition Def(string kind, params (string Role, string Target)[] links) => new()
    {
        Kind = kind,
        Links = links.Select(l => new KnowledgeLink { Role = l.Role, Target = l.Target }).ToList(),
    };

    /// <summary>目标解析 fake：目标名 → 项（找不到 null=断链）。</summary>
    private static KnowledgeItem? Resolve(
        IReadOnlyDictionary<string, KnowledgeItem> index, string target, string _)
        => index.TryGetValue(target, out var item) ? item : null;

    [Fact]
    public void Broken_link_reports_error()
    {
        var items = new List<KnowledgeItem> { new("场景/a", Def("业务场景", ("uses", "不存在的目标"))) };
        var diagnostics = KnowledgeGraphValidator.Validate(items,
            (t, s) => Resolve(new Dictionary<string, KnowledgeItem>(), t, s));

        var broken = Assert.Single(diagnostics);
        Assert.Equal("error", broken.Severity);
        Assert.Equal("断链", broken.Category);
        Assert.Equal("场景/a", broken.File);
        Assert.Contains("不存在的目标", broken.Message);
    }

    [Fact]
    public void Resolved_corpus_produces_no_diagnostics()
    {
        // kind/role 任意自由字符串（规范走各项目 README）；全部 link 解析得到 → 0 诊断。
        var items = new List<KnowledgeItem>
        {
            new("场景/a", Def("随便什么kind", ("随便什么role", "认知/c"))),
            new("认知/c", Def("另一个kind", ("derived_from", "特征/b"))),
            new("特征/b", Def("第三个kind")),
        };
        var index = items.ToDictionary(i => i.Path, i => i);
        var diagnostics = KnowledgeGraphValidator.Validate(items, (t, s) => Resolve(index, t, s));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Unparseable_source_skips_link_checks()
    {
        // 解析失败的文件（Def=null）links 不可信——自身不产断链诊断（仍被别的文件当解析目标）。
        var items = new List<KnowledgeItem> { new("坏文件/a", null) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, (t, s) => null);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Link_to_unparseable_target_is_not_broken()
    {
        // 目标文件存在但解析失败（Definition=null）→ 算解析到，不断链。
        var items = new List<KnowledgeItem> { new("场景/a", Def("业务场景", ("uses", "坏文件/a"))) };
        var index = new Dictionary<string, KnowledgeItem> { ["坏文件/a"] = new("坏文件/a", null) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, (t, s) => Resolve(index, t, s));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Blank_link_target_is_skipped()
    {
        // 空白 target（无意义的 link）跳过，不当断链报。
        var items = new List<KnowledgeItem> { new("场景/a", Def("业务场景", ("uses", "  "))) };
        var diagnostics = KnowledgeGraphValidator.Validate(items, (t, s) => null);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Multiple_broken_links_all_reported()
    {
        var items = new List<KnowledgeItem>
        {
            new("场景/a", Def("业务场景", ("uses", "目标1"), ("then", "目标2"))),
            new("场景/b", Def("业务场景", ("uses", "目标3"))),
        };
        var diagnostics = KnowledgeGraphValidator.Validate(items, (t, s) => null);

        Assert.Equal(3, diagnostics.Count(d => d.Category == "断链"));
        Assert.Equal(2, diagnostics.Select(d => d.File).Distinct().Count()); // 两个源文件
    }
}
