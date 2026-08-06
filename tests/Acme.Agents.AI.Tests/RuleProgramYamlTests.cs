using System.Text.Json;
using Acme.Agents.AI;
using Aether.AI.Core.Agents;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="RuleProgramYaml"/>：YAML 程序（规则 agent 笔记 body）→ <see cref="RuleProgram"/>（Core DTO）转换。
/// 验证结构化 <c>args</c> map→<c>ArgsJson</c> JSON、<c>minToolCalls</c>/<c>replyText</c>/
/// <c>noMatchReply</c>/<c>maxSteps</c>、嵌套 args 规整、非法/空 YAML→空程序。平台 seam（JSON）与
/// <c>RuleProgramBrain</c> 不动，故这里只覆盖桥接层规整逻辑。
/// </summary>
public class RuleProgramYamlTests
{
    [Fact]
    public void Deserialize_maps_args_map_to_argsjson_json()
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
        // args map → JSON 串，模板占位符原样保留（替换在 brain 运行时做）
        using var doc = JsonDocument.Parse(step.Action.ArgsJson!);
        Assert.Equal("{user.1}", doc.RootElement.GetProperty("text").GetString());
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
        using var doc = JsonDocument.Parse(step.Action.ArgsJson!);

        // 嵌套数组规整：IList → JSON array，元素保留
        var symbols = doc.RootElement.GetProperty("symbols");
        Assert.Equal(JsonValueKind.Array, symbols.ValueKind);
        Assert.Equal("600000", symbols[0].GetString());
        Assert.Equal("000001", symbols[1].GetString());

        // 嵌套对象规整：Dictionary<object,object> → JSON object，键保留
        var opts = doc.RootElement.GetProperty("opts");
        Assert.Equal(JsonValueKind.Object, opts.ValueKind);
        Assert.Equal("10", opts.GetProperty("limit").GetString());
        Assert.Equal("true", opts.GetProperty("strict").GetString());
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

    [Fact]
    public void TrySerializeToJson_round_trips_through_json_seam()
    {
        var yaml = """
                   steps:
                     - match:
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

        Assert.True(RuleProgramYaml.TrySerializeToJson(yaml, out var json));
        Assert.NotNull(json);

        // JSON 串能反序列化回 RuleProgram（平台 seam 消费侧）
        var program = JsonSerializer.Deserialize<RuleProgram>(json!);
        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);
        Assert.Equal("Echo", program.Steps[0].Action.Tool);
    }

    [Fact]
    public void TrySerializeToJson_returns_false_for_illegal_yaml()
    {
        Assert.False(RuleProgramYaml.TrySerializeToJson("steps: [unclosed", out var json));
        Assert.Null(json);
    }
}
