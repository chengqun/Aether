using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Infrastructure.Agents;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="RuleSourceParser"/>：单个 YAML 文档（元数据 + 规则程序顶层共存）↔ <see cref="CustomAgentDefinition"/>。
/// 验证元数据映射、<c>Engine="Rule"</c> 硬编码、<c>SystemPrompt</c>=程序部分（可被 <see cref="RuleProgramYaml"/> 解析）、
/// Serialize 往返、非法/缺 name→false。
/// </summary>
public class RuleSourceParserTests
{
    private static readonly RuleSourceParser _parser = new();

    private const string Sample = """
                                  name: 查询规则
                                  description: 查(\\d+) → Echo 回显
                                  category: 规则
                                  tools:
                                    - Echo
                                  toolApprovals:
                                    Echo: Confirm
                                  memory: Off
                                  maxHistory: 20
                                  noMatchReply: 无匹配规则
                                  maxSteps: 5
                                  steps:
                                    - name: 查询
                                      match:
                                        onUserRegex: '查(\d+)'
                                      action:
                                        tool: Echo
                                        args:
                                          text: '{user.1}'
                                    - match:
                                        minToolCalls: 1
                                      action:
                                        replyText: '结果：{result}'
                                  """;

    [Fact]
    public void TryParse_maps_metadata_and_hardcodes_engine_rule()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out var error));
        Assert.NotNull(def);
        Assert.Empty(error);

        Assert.Equal("查询规则", def!.Name);
        Assert.Equal("规则", def.Category);
        Assert.Equal("Rule", def.Engine);
        Assert.Equal(NoteNodeType.Rule, def.NodeType);
        Assert.Equal(AgentMemoryMode.Off, def.Memory);
        Assert.Equal(20, def.MaxHistory);
        Assert.Equal(new[] { "Echo" }, def.Tools);
        Assert.Equal("Confirm", def.ToolApprovals["Echo"]);
    }

    [Fact]
    public void TryParse_puts_program_part_into_system_prompt_parseable_by_rule_program_yaml()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out _));
        Assert.NotNull(def);

        // SystemPrompt 是程序部分（steps/noMatchReply/maxSteps），不含元数据 → RuleProgramYaml 能解析
        Assert.True(RuleProgramYaml.TryDeserialize(def!.SystemPrompt, out var program));
        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);
        Assert.Equal("Echo", program.Steps[0].Action.Tool);
        Assert.Equal("无匹配规则", program.NoMatchReply);
        Assert.Equal(5, program.MaxSteps);
    }

    [Fact]
    public void Serialize_round_trips_metadata_and_program()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out _));
        Assert.NotNull(def);

        var yaml = _parser.Serialize(def!);
        // 往返后再解析：元数据 + 程序一致
        Assert.True(_parser.TryParse(yaml, out var def2, out _));
        Assert.NotNull(def2);
        Assert.Equal(def!.Name, def2!.Name);
        Assert.Equal(def.Category, def2.Category);
        Assert.Equal("Rule", def2.Engine);
        Assert.Equal(NoteNodeType.Rule, def2.NodeType);
        Assert.Equal(def.Tools, def2.Tools);
        Assert.Equal(def.ToolApprovals["Echo"], def2.ToolApprovals["Echo"]);
        Assert.Equal(def.MaxHistory, def2.MaxHistory);

        // 程序部分往返后仍可解析为 RuleProgram，结构一致
        Assert.True(RuleProgramYaml.TryDeserialize(def2.SystemPrompt, out var prog2));
        Assert.Equal(2, prog2!.Steps.Count);
        Assert.Equal("Echo", prog2.Steps[0].Action.Tool);
    }

    [Fact]
    public void Serialize_is_single_yaml_document_without_front_matter_or_engine()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out _));
        var yaml = _parser.Serialize(def!);

        Assert.DoesNotContain("---", yaml);     // 单文档，无 front-matter 分隔
        Assert.DoesNotContain("engine:", yaml); // engine 由解析器硬编码，不入文件
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_returns_false_for_empty(string source)
    {
        Assert.False(_parser.TryParse(source, out var def, out var error));
        Assert.Null(def);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_returns_false_for_missing_name()
    {
        var noName = """
                     category: 规则
                     steps: []
                     """;
        Assert.False(_parser.TryParse(noName, out var def, out var error));
        Assert.Null(def);
        Assert.Contains("name", error);
    }

    [Fact]
    public void TryParse_returns_false_for_illegal_yaml()
    {
        Assert.False(_parser.TryParse("steps: [unclosed", out var def, out var error));
        Assert.Null(def);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_defaults_category_and_memory_when_missing()
    {
        var minimal = """
                      name: 裸规则
                      steps:
                        - match:
                            onUserRegex: 'hi'
                          action:
                            replyText: hello
                      """;
        Assert.True(_parser.TryParse(minimal, out var def, out _));
        Assert.NotNull(def);
        Assert.Equal("规则", def!.Category);   // 缺省“规则”
        Assert.Equal(AgentMemoryMode.Off, def.Memory); // 缺省 Off（规则无 LLM 记忆）
        Assert.Equal(20, def.MaxHistory);     // 缺省 20
        Assert.False(def.RequireToolApproval); // 缺省 false（不开审批）
    }

    [Fact]
    public void Serialize_round_trips_require_tool_approval()
    {
        Assert.True(_parser.TryParse(Sample, out var def, out _));
        Assert.NotNull(def);
        Assert.False(def!.RequireToolApproval); // Sample 未开审批

        def.RequireToolApproval = true;
        var yaml = _parser.Serialize(def);
        Assert.Contains("requireToolApproval: true", yaml);

        // 往返：重解析应保留 true
        Assert.True(_parser.TryParse(yaml, out var def2, out _));
        Assert.NotNull(def2);
        Assert.True(def2!.RequireToolApproval);

        // 关掉再往返：应回落 false，且序列化不输出该键（OmitNull）
        def2.RequireToolApproval = false;
        var yaml2 = _parser.Serialize(def2);
        Assert.DoesNotContain("requireToolApproval", yaml2);
    }
}
