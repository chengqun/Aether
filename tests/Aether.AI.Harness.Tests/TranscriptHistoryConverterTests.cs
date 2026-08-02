using Aether.AI.Core.Messages;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 验证 <see cref="TranscriptHistoryConverter"/> 正确还原 LLM 历史——尤其 tool 配对不产生孤儿、
/// 空 content assistant（reasoning/plan）被跳过（否则 GLM/Anthropic 兼容端点 400）。
/// </summary>
public class TranscriptHistoryConverterTests
{
    [Fact]
    public void User_and_assistant_round_trip()
    {
        var messages = new List<Message>
        {
            Message.User("你好"),
            Message.Assistant("你好！有什么可以帮你？"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal("你好", result[0].Text);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal("你好！有什么可以帮你？", result[1].Text);
    }

    [Fact]
    public void Reasoning_and_plan_are_skipped()
    {
        var messages = new List<Message>
        {
            Message.User("查天气"),
            new Message { Role = MessageRole.Assistant, Content = "让我想想", Metadata = new Dictionary<string, string> { ["kind"] = "reasoning" } },
            new Message { Role = MessageRole.Assistant, Content = "", Metadata = new Dictionary<string, string> { ["kind"] = "plan", ["todos"] = "[]" } },
            Message.Assistant("北京晴"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        Assert.Equal(2, result.Count); // 只剩 user + answer，reasoning/plan 跳过
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
    }

    [Fact]
    public void Empty_content_assistant_is_skipped()
    {
        var messages = new List<Message>
        {
            Message.User("hi"),
            new Message { Role = MessageRole.Assistant, Content = "" }, // 空 content → 跳过
            Message.Assistant("hello"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        Assert.Equal(2, result.Count);
        Assert.Equal("hello", result[1].Text);
    }

    [Fact]
    public void Tool_step_becomes_paired_call_and_result()
    {
        var messages = new List<Message>
        {
            Message.User("查天气"),
            new Message
            {
                Role = MessageRole.Tool,
                ToolName = "GetWeather",
                ToolCallId = "call-1",
                Content = "",
                Metadata = new Dictionary<string, string> { ["kind"] = "tool", ["input"] = "{\"city\":\"北京\"}", ["output"] = "{\"result\":\"晴\"}" },
            },
            Message.Assistant("北京晴"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        // user → assistant(FunctionCall) → user(FunctionResult) → assistant(answer) = 4
        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);

        // tool_use: assistant + FunctionCallContent
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        var call = result[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("GetWeather", call.Name);

        // tool_result: user + FunctionResultContent（callId 匹配、相邻）
        Assert.Equal(ChatRole.User, result[2].Role);
        var res = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call-1", res.CallId);

        // answer
        Assert.Equal(ChatRole.Assistant, result[3].Role);
        Assert.Equal("北京晴", result[3].Text);
    }

    [Fact]
    public void No_orphan_tool_result_every_call_has_result()
    {
        var messages = new List<Message>
        {
            Message.User("do two things"),
            ToolMsg("call-a", "ToolA", "{\"x\":1}", "{\"r\":1}"),
            ToolMsg("call-b", "ToolB", "{\"y\":2}", "{\"r\":2}"),
            Message.Assistant("done"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        // user → callA → resultA → callB → resultB → answer = 6
        Assert.Equal(6, result.Count);
        var calls = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        var results = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, results.Count);
        // 每个 call 都有对应的 result（callId 匹配）
        foreach (var c in calls)
            Assert.Contains(results, r => r.CallId == c.CallId);
        // 每个 result 都有对应的 call（无孤儿）
        foreach (var r in results)
            Assert.Contains(calls, c => c.CallId == r.CallId);
    }

    private static Message ToolMsg(string callId, string name, string input, string output) => new()
    {
        Role = MessageRole.Tool,
        ToolName = name,
        ToolCallId = callId,
        Content = "",
        Metadata = new Dictionary<string, string> { ["kind"] = "tool", ["input"] = input, ["output"] = output },
    };
}
