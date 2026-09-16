using Acme.Agents.AI.Evals;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="EvalSourceParser"/>：单个 YAML 文档（camelCase 键）→ <see cref="EvalDefinition"/>。
/// 验证全字段映射、断言归一化（空列表/空白串→null）、缺 name/agentId/cases/query 与空文件/坏 YAML → false、
/// 未知键容忍（IgnoreUnmatchedProperties，同 RuleSourceParser builder 配置）。
/// </summary>
public class EvalSourceParserTests
{
    private static readonly EvalSourceParser _parser = new();

    private const string Sample = """
                                  name: 客服规则回归
                                  description: 客服规则 agent 的回归评测
                                  agentId: 3f2b8a1e-0000-4000-8000-000000000001
                                  cases:
                                    - query: 你好
                                      expectedKeywords: [你好, 欢迎语]
                                      expectedOutput: null
                                      expectedTools: []
                                      minLength: 1
                                    - query: 查询订单
                                      expectedTools: [QueryOrder]
                                  """;

    private static readonly Guid AgentId = Guid.Parse("3f2b8a1e-0000-4000-8000-000000000001");

    [Fact]
    public void TryParse_maps_all_fields()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out var error));
        Assert.NotNull(def);
        Assert.Empty(error);

        Assert.Equal("客服规则回归", def!.Name);
        Assert.Equal("客服规则 agent 的回归评测", def.Description);
        Assert.Equal(AgentId, def.AgentId);
        Assert.Equal(2, def.Cases.Count);

        var first = def.Cases[0];
        Assert.Equal("你好", first.Query);
        Assert.Equal(new[] { "你好", "欢迎语" }, first.ExpectedKeywords);
        Assert.Null(first.ExpectedOutput);
        Assert.Null(first.ExpectedTools); // 空列表归一化为 null（未配置）
        Assert.Equal(1, first.MinLength);

        var second = def.Cases[1];
        Assert.Equal("查询订单", second.Query);
        Assert.Null(second.ExpectedKeywords);
        Assert.Null(second.ExpectedOutput);
        Assert.Equal(new[] { "QueryOrder" }, second.ExpectedTools);
        Assert.Null(second.MinLength);
    }

    [Fact]
    public void TryParse_minimal_suite_succeeds()
    {
        const string minimal = """
                               name: 最小套件
                               agentId: 3f2b8a1e-0000-4000-8000-000000000001
                               cases:
                                 - query: hi
                               """;
        Assert.True(_parser.TryParse(minimal, out var def, out var error));
        Assert.NotNull(def);
        Assert.Empty(error);
        Assert.Equal("最小套件", def!.Name);
        Assert.Null(def.Description);
        Assert.Single(def.Cases);
        // 全部断言未配置 → 均为 null（case 恒通过，仅采集回复）
        Assert.Null(def.Cases[0].ExpectedKeywords);
        Assert.Null(def.Cases[0].ExpectedOutput);
        Assert.Null(def.Cases[0].ExpectedTools);
        Assert.Null(def.Cases[0].MinLength);
    }

    // —— 缺字段 / 非法输入 → false + 中文错误 ——

    [Fact]
    public void TryParse_missing_name_fails()
    {
        const string src = """
                            agentId: 3f2b8a1e-0000-4000-8000-000000000001
                            cases:
                              - query: hi
                            """;
        Assert.False(_parser.TryParse(src, out var def, out var error));
        Assert.Null(def);
        Assert.Equal("缺少 name 字段", error);
    }

    [Fact]
    public void TryParse_missing_agentId_fails()
    {
        const string src = """
                            name: 套件
                            cases:
                              - query: hi
                            """;
        Assert.False(_parser.TryParse(src, out var def, out var error));
        Assert.Null(def);
        Assert.Equal("缺少 agentId 字段（被评 agent 的 Guid）", error);
    }

    [Fact]
    public void TryParse_empty_guid_agentId_fails()
    {
        const string src = """
                            name: 套件
                            agentId: 00000000-0000-0000-0000-000000000000
                            cases:
                              - query: hi
                            """;
        Assert.False(_parser.TryParse(src, out _, out var error));
        Assert.Equal("缺少 agentId 字段（被评 agent 的 Guid）", error);
    }

    [Fact]
    public void TryParse_empty_cases_fails()
    {
        const string src = """
                            name: 套件
                            agentId: 3f2b8a1e-0000-4000-8000-000000000001
                            cases: []
                            """;
        Assert.False(_parser.TryParse(src, out _, out var error));
        Assert.Equal("缺少 cases 字段（至少一条用例）", error);
    }

    [Fact]
    public void TryParse_case_missing_query_fails_with_index()
    {
        const string src = """
                            name: 套件
                            agentId: 3f2b8a1e-0000-4000-8000-000000000001
                            cases:
                              - query: 第一条
                              - expectedKeywords: [x]
                            """;
        Assert.False(_parser.TryParse(src, out _, out var error));
        Assert.Equal("cases[1] 缺少 query 字段", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void TryParse_empty_source_fails(string source)
    {
        Assert.False(_parser.TryParse(source, out var def, out var error));
        Assert.Null(def);
        Assert.Equal("评测文件为空", error);
    }

    [Fact]
    public void TryParse_bad_yaml_fails()
    {
        const string bad = """
                           name: 坏格式
                           agentId: [未闭合
                           cases:
                             - query: hi
                           """;
        Assert.False(_parser.TryParse(bad, out var def, out var error));
        Assert.Null(def);
        Assert.StartsWith("评测 YAML 解析失败：", error);
    }

    [Fact]
    public void TryParse_unknown_keys_tolerated()
    {
        const string src = """
                            name: 套件
                            agentId: 3f2b8a1e-0000-4000-8000-000000000001
                            version: 2
                            unknownTopLevel: whatever
                            cases:
                              - query: hi
                                extraCaseKey: 1
                            """;
        Assert.True(_parser.TryParse(src, out var def, out var error));
        Assert.NotNull(def);
        Assert.Empty(error);
        Assert.Equal("hi", def!.Cases[0].Query);
    }
}
