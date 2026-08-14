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
            TranscriptHistoryConverterTests.ToolMsg("call-a", "ToolA", "{\"x\":1}", "{\"r\":1}"),
            TranscriptHistoryConverterTests.ToolMsg("call-b", "ToolB", "{\"y\":2}", "{\"r\":2}"),
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

    internal static Message ToolMsg(string callId, string name, string input, string output) => new()
    {
        Role = MessageRole.Tool,
        ToolName = name,
        ToolCallId = callId,
        Content = "",
        Metadata = new Dictionary<string, string> { ["kind"] = "tool", ["input"] = input, ["output"] = output },
    };
}

/// <summary>
/// <see cref="TranscriptHistoryConverter.Sanitize"/> 直接测试——验证「重启续聊 400」的四类诱因被修复。
/// </summary>
public class TranscriptHistorySanitizeTests
{
    [Fact]
    public void Trailing_tool_result_without_answer_is_trimmed_to_assistant()
    {
        // 上一轮以 tool_result 收尾、无最终回答（审批挂起 / 中断）→ 还原后尾部是 user(tool_result)。
        // 框架追加新 user 消息会与它连续→400。Sanitize 应裁剪到以 assistant 结尾。
        var messages = new List<Message>
        {
            Message.User("查天气"),
            TranscriptHistoryConverterTests.ToolMsg("call-1", "GetWeather", "{\"city\":\"北京\"}", "{\"result\":\"晴\"}"),
            // 无最终 assistant 回答
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        // 整段不完整轮次被裁剪→空历史（user 问题也删，因为无答案可保）
        Assert.Empty(result);
    }

    [Fact]
    public void Trailing_tool_result_after_complete_prior_turn_keeps_prior_turn()
    {
        // 有完整上一轮答案 + 不完整本轮（tool 无答案）→ 保留完整轮，裁剪不完整轮。
        var messages = new List<Message>
        {
            Message.User("q1"),
            Message.Assistant("a1"),
            Message.User("q2"),
            TranscriptHistoryConverterTests.ToolMsg("call-1", "GetWeather", "{}", "{\"r\":1}"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        // 保留 q1→a1；q2 + tool 被裁剪
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal("q1", result[0].Text);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal("a1", result[1].Text);
    }

    [Fact]
    public void Trailing_tool_with_assistant_narration_keeps_narration()
    {
        // 上一轮：assistant 旁白 + tool_use + tool_result，无最终回答。
        // 裁剪 tool_result 后，旁白保留（assistant text 安全），tool_use 沦孤儿被删。
        var messages = new List<Message>
        {
            Message.User("查天气"),
            Message.Assistant("让我查一下"), // 旁白
            TranscriptHistoryConverterTests.ToolMsg("call-1", "GetWeather", "{}", "{\"r\":1}"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        // user(q) → assistant("让我查一下")；tool 段裁剪
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal("让我查一下", result[1].Text);
        Assert.Empty(result[1].Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public void Empty_callId_is_synthesized_and_pair_stays_matched()
    {
        // 持久化丢失 CallId（ToolCallId=null）→ 还原配对但 id 空 → 端点拒。
        // Sanitize 应合成非空 id 且 call↔result 共享。
        var messages = new List<Message>
        {
            Message.User("q"),
            new Message
            {
                Role = MessageRole.Tool,
                ToolName = "GetWeather",
                ToolCallId = null, // 丢失
                Content = "",
                Metadata = new Dictionary<string, string> { ["kind"] = "tool", ["input"] = "{}", ["output"] = "{\"r\":1}" },
            },
            Message.Assistant("done"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        var call = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Single();
        var res = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.False(string.IsNullOrEmpty(call.CallId));
        Assert.Equal(call.CallId, res.CallId);
    }

    [Fact]
    public void Null_tool_output_gets_placeholder()
    {
        // 审批挂起后持久化 output="" → 还原为 null result → 端点拒空 tool_result。Sanitize 占位。
        var messages = new List<Message>
        {
            Message.User("q"),
            new Message
            {
                Role = MessageRole.Tool,
                ToolName = "GetWeather",
                ToolCallId = "call-1",
                Content = "",
                Metadata = new Dictionary<string, string> { ["kind"] = "tool", ["input"] = "{}", ["output"] = "" },
            },
            Message.Assistant("done"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        var res = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.NotNull(res.Result);
        Assert.NotEqual("", res.Result);
    }

    [Fact]
    public void Orphan_tool_use_in_accumulator_is_dropped()
    {
        // 内存累积路径：模型发了 tool_use 但无 tool_result（审批挂起 / 出错）→ 孤儿 call。
        var orphan = new ChatMessage { Role = ChatRole.Assistant };
        orphan.Contents.Add(new FunctionCallContent("call-x", "ToolX", null));

        var result = TranscriptHistoryConverter.Sanitize(new[] { orphan });

        Assert.Empty(result); // 孤儿 call 无 result → 删，消息变空 → 删
    }

    [Fact]
    public void Consecutive_user_messages_are_merged()
    {
        // 持久化遗留连续 user（某轮 agent 完全无输出）→ 角色不交替。Sanitize 合并。
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "q1"),
            new(ChatRole.User, "q2"),
            new(ChatRole.Assistant, "a"),
        };

        var result = TranscriptHistoryConverter.Sanitize(history);

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        // 两个 user text 合并到一条
        Assert.Contains("q1", result[0].Text);
        Assert.Contains("q2", result[0].Text);
    }

    [Fact]
    public void Complete_history_is_unchanged()
    {
        // 正常完整历史（以 assistant 结尾）→ 不裁剪、不合并，保持原样。
        var messages = new List<Message>
        {
            Message.User("q1"),
            Message.Assistant("a1"),
            Message.User("q2"),
            Message.Assistant("a2"),
        };

        var result = TranscriptHistoryConverter.Convert(messages);

        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.Assistant, result[^1].Role);
    }

    [Fact]
    public void Sanitize_is_idempotent()
    {
        var messages = new List<Message>
        {
            Message.User("q"),
            TranscriptHistoryConverterTests.ToolMsg("call-1", "GetWeather", "{}", "{\"r\":1}"),
            Message.Assistant("done"),
        };

        var once = TranscriptHistoryConverter.Convert(messages);
        var twice = TranscriptHistoryConverter.Sanitize(once);

        Assert.Equal(once.Count, twice.Count);
    }
}

/// <summary>
/// <see cref="TranscriptHistoryConverter.SanitizeProviderHistory"/> 专用测试——活路径
/// <c>RestoringChatHistoryProvider.ProvideChatHistoryAsync</c> 用它净化历史。
/// 核心回归守卫：活路径 FICC 循环里，最近一轮的 tool_call 在 Provide 时尚无 result
/// （result 下一轮 Store 才落地），<see cref="TranscriptHistoryConverter.Sanitize"/>/
/// <see cref="TranscriptHistoryConverter.SanitizeLive"/> 会把它当孤儿丢弃 → 下一轮请求变
/// 「有 result 无 call」→ GLM 重调同一工具（每个工具执行两遍）。SanitizeProviderHistory 必须保留它。
/// </summary>
public class TranscriptHistoryProviderSanitizeTests
{
    [Fact]
    public void InProgress_tool_call_without_result_is_preserved()
    {
        // 活路径：assistant 刚发了 tool_call，result 尚未落地（FICC 即将执行/已执行但下一轮才 Store）。
        var assistant = new ChatMessage { Role = ChatRole.Assistant };
        assistant.Contents.Add(new TextContent("我来查询"));
        assistant.Contents.Add(new FunctionCallContent("call-1", "QuerySecurity",
            new Dictionary<string, object?> { ["keyword"] = "大全能源" }));
        var history = new List<ChatMessage> { new(ChatRole.User, "查大全能源"), assistant };

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(history);

        // tool_call 必须保留——否则下一轮请求变孤儿 result → 模型重调工具（重复执行）。
        var call = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Single();
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("QuerySecurity", call.Name);
    }

    [Fact]
    public void Sanitize_drops_in_progress_call_but_provider_sanitize_keeps_it()
    {
        // 对照：Sanitize / SanitizeLive 把「无 result 的 call」当孤儿丢弃（工具重复执行的根因）；
        // SanitizeProviderHistory 保留。这是本 bug 的核心回归守卫。
        var msg = new ChatMessage { Role = ChatRole.Assistant };
        msg.Contents.Add(new FunctionCallContent("call-1", "QuerySecurity", null));

        Assert.Empty(TranscriptHistoryConverter.Sanitize(new[] { msg })
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
        Assert.Empty(TranscriptHistoryConverter.SanitizeLive(new[] { msg })
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
        Assert.Single(TranscriptHistoryConverter.SanitizeProviderHistory(new[] { msg })
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }

    [Fact]
    public void Orphan_tool_result_without_call_is_still_dropped()
    {
        // 孤儿 tool_result（无前置 call）仍是结构非法（OpenAI 兼容端点会 400）→ 丢弃。
        var msg = new ChatMessage { Role = ChatRole.Tool };
        msg.Contents.Add(new FunctionResultContent("call-1", "result"));

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(new[] { msg });

        Assert.Empty(result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
    }

    [Fact]
    public void Paired_call_and_result_pass_through_unchanged()
    {
        var call = new ChatMessage { Role = ChatRole.Assistant };
        call.Contents.Add(new FunctionCallContent("call-1", "Tool", null));
        var res = new ChatMessage { Role = ChatRole.Tool };
        res.Contents.Add(new FunctionResultContent("call-1", "r"));

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(new[] { call, res });

        Assert.Single(result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
        Assert.Single(result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
    }

    [Fact]
    public void Provider_sanitize_is_idempotent()
    {
        var assistant = new ChatMessage { Role = ChatRole.Assistant };
        assistant.Contents.Add(new FunctionCallContent("call-1", "Tool", null));
        var history = new List<ChatMessage> { new(ChatRole.User, "q"), assistant };

        var once = TranscriptHistoryConverter.SanitizeProviderHistory(history);
        var twice = TranscriptHistoryConverter.SanitizeProviderHistory(once);

        Assert.Equal(once.Count, twice.Count);
        Assert.Single(twice.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }
}
