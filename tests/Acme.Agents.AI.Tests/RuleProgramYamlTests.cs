using System.Collections;
using Acme.Agents.AI;
using Aether.AI.Core.Agents;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="RuleProgramYaml"/>：YAML 程序（规则 agent 笔记 body）→ <see cref="RuleProgram"/>（Core DTO）转换。
/// 验证结构化 <c>args</c> map→<c>Args</c> 字典、<c>minToolCalls</c>/<c>replyText</c>/
/// <c>noMatchReply</c>/<c>maxSteps</c>、程序级 <c>capture</c>、嵌套 args 规整、非法/空 YAML→空程序。
/// 平台 seam 现为对象直传（不经 JSON），故这里只覆盖桥接层规整逻辑。
/// </summary>
public class RuleProgramYamlTests
{
    [Fact]
    public void Deserialize_maps_args_map_to_args_dict()
    {
        var yaml = """
                   steps:
                     - name: 查询
                       match:
                         onUserRegex: '查(?<sym>\d+)'
                       action:
                         tool: Echo
                         args:
                           text: '{user.1}'
                   """;

        Assert.True(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.NotNull(program);
        var step = Assert.Single(program!.Steps);
        Assert.Equal("Echo", step.Action.Tool);
        // args map → 字典，模板占位符原样保留（替换在 brain 运行时按值做）
        Assert.NotNull(step.Action.Args);
        Assert.Equal("{user.1}", step.Action.Args!["text"]?.ToString());
    }

    [Fact]
    public void Deserialize_maps_minToolCalls_replyText_noMatchReply_maxSteps()
    {
        var yaml = """
                   noMatchReply: 无匹配规则
                   maxSteps: 5
                   steps:
                     - match:
                         minToolCalls: 1
                       action:
                         replyText: '结果：{result}'
                   """;

        Assert.True(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.NotNull(program);
        Assert.Equal("无匹配规则", program!.NoMatchReply);
        Assert.Equal(5, program.MaxSteps);

        var step = Assert.Single(program.Steps);
        Assert.Equal(1, step.Match.MinToolCalls);
        Assert.Null(step.Match.OnUserRegex);
        Assert.Equal("结果：{result}", step.Action.ReplyText);
        Assert.Null(step.Action.Tool);
    }

    [Fact]
    public void Deserialize_normalizes_nested_args()
    {
        var yaml = """
                   steps:
                     - action:
                         tool: Multi
                         args:
                           symbols:
                             - '600000'
                             - '000001'
                           opts:
                             limit: '10'
                             strict: 'true'
                   """;

        Assert.True(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.NotNull(program);
        var step = Assert.Single(program!.Steps);
        Assert.NotNull(step.Action.Args);

        // 嵌套数组规整：IList → List<object?>
        var symbols = Assert.IsAssignableFrom<IList>(step.Action.Args!["symbols"]);
        Assert.Equal("600000", symbols[0]?.ToString());
        Assert.Equal("000001", symbols[1]?.ToString());

        // 嵌套对象规整：Dictionary<object,object> → Dictionary<string,object?>
        var opts = Assert.IsAssignableFrom<IDictionary<string, object?>>(step.Action.Args!["opts"]);
        Assert.Equal("10", opts["limit"]?.ToString());
        Assert.Equal("true", opts["strict"]?.ToString());
    }

    [Fact]
    public void Deserialize_maps_program_level_capture()
    {
        var yaml = """
                   capture: '读(.+)'
                   noMatchReply: 无匹配规则
                   steps:
                     - match:
                         maxToolCalls: 0
                       action:
                         tool: Read
                         args:
                           filePath: '{user.1}'
                   """;

        Assert.True(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.NotNull(program);
        Assert.Equal("读(.+)", program!.Capture);
        var step = Assert.Single(program.Steps);
        Assert.Equal("Read", step.Action.Tool);
        Assert.Equal("{user.1}", step.Action.Args!["filePath"]?.ToString());
    }

    [Fact]
    public void Deserialize_defaults_maxSteps_when_missing_or_invalid()
    {
        var yaml = """
                   steps: []
                   """;
        Assert.True(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.NotNull(program);
        Assert.Equal(20, program!.MaxSteps); // 缺省 20
        Assert.Empty(program.Steps);
    }

    [Theory]
    [InlineData("")]          // 空
    [InlineData("   ")]       // 空白
    [InlineData(null)]        // null
    public void Deserialize_returns_false_for_empty(string? yaml)
    {
        Assert.False(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.Null(program);
    }

    [Fact]
    public void Deserialize_returns_false_for_illegal_yaml()
    {
        // 非法 YAML（映射值是裸标量冒号冲突）→ false + null，不抛
        var yaml = "steps: [unclosed";
        Assert.False(RuleProgramYaml.TryDeserialize(yaml, out var program));
        Assert.Null(program);
    }
}
