using Acme.Agents.AI.Evals;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="EvalCaseRecorder"/>：四个固定检查（关键词/期望输出/工具调用/非空）按 query 反查 case 断言——
/// 未配置不记录（零噪音）、配置了才记判定；工具调用从会话 <see cref="FunctionCallContent"/> 提取
/// （<c>item.Tools</c> 是"可用工具"非"已调用"，MAF 语义）；判定顺序保持 case 原始顺序。
/// </summary>
public class EvalCaseRecorderTests
{
    private static List<EvalCaseRecorder.CaseRecord> Run(EvalCaseRecorder recorder, params EvalItem[] items)
    {
        var checks = recorder.BuildChecks();
        foreach (var item in items)
            foreach (var check in checks)
                check(item);
        return recorder.GetResults().ToList();
    }

    private static EvalCase Case(string query, List<string>? keywords = null, string? expectedOutput = null,
        List<string>? tools = null, int? minLength = null) => new()
    {
        Query = query,
        ExpectedKeywords = keywords,
        ExpectedOutput = expectedOutput,
        ExpectedTools = tools,
        MinLength = minLength,
    };

    // —— 关键词 ——

    [Fact]
    public void Keywords_all_present_passes()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("你好", keywords: ["你好", "欢迎"]) });
        var results = Run(recorder, new EvalItem("你好", "你好，欢迎光临！"));

        var rec = Assert.Single(results);
        Assert.True(rec.Passed);
        var check = Assert.Single(rec.Checks, c => c.CheckName == "关键词");
        Assert.True(check.Passed);
        Assert.Contains("包含全部关键词", check.Reason);
    }

    [Fact]
    public void Keywords_missing_fails_with_missing_list()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", keywords: ["甲", "乙"]) });
        var results = Run(recorder, new EvalItem("q", "只有甲"));

        var rec = Assert.Single(results);
        Assert.False(rec.Passed);
        var check = Assert.Single(rec.Checks, c => c.CheckName == "关键词");
        Assert.False(check.Passed);
        Assert.Contains("乙", check.Reason);
    }

    [Fact]
    public void Keywords_match_is_case_insensitive()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", keywords: ["HELLO"]) });
        var results = Run(recorder, new EvalItem("q", "well hello there"));

        Assert.True(Assert.Single(results).Passed);
    }

    // —— 期望输出 ——

    [Theory]
    [InlineData("订单已查询", true)]
    [InlineData("完全无关的回复", false)]
    public void ExpectedOutput_substring_match_decides_verdict(string response, bool expectedPass)
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", expectedOutput: "订单已查询") });
        var results = Run(recorder, new EvalItem("q", response));

        Assert.Equal(expectedPass, Assert.Single(results).Passed);
    }

    // —— 工具调用（从会话 FunctionCallContent 提取，非 item.Tools）——

    private static EvalItem ItemWithToolCalls(params string[] toolNames)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "q") };
        foreach (var name in toolNames)
            messages.Add(new ChatMessage(ChatRole.Assistant,
                new AIContent[] { new FunctionCallContent($"call_{name}", name) })); // ctor 参数序：(callId, name, args)
        messages.Add(new ChatMessage(ChatRole.Assistant, "执行完毕"));
        return new EvalItem("q", "执行完毕", messages);
    }

    [Fact]
    public void Tools_called_in_conversation_passes()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", tools: ["QueryOrder", "Notify"]) });
        var results = Run(recorder, ItemWithToolCalls("QueryOrder", "Notify"));

        var rec = Assert.Single(results);
        Assert.True(rec.Passed);
        Assert.True(Assert.Single(rec.Checks, c => c.CheckName == "工具调用").Passed);
    }

    [Fact]
    public void Tools_match_is_case_insensitive()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", tools: ["QUERYORDER"]) });
        var results = Run(recorder, ItemWithToolCalls("QueryOrder"));

        Assert.True(Assert.Single(results).Passed);
    }

    [Fact]
    public void Tools_not_called_fails_with_actual_list()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", tools: ["甲", "乙"]) });
        var results = Run(recorder, ItemWithToolCalls("甲"));

        var rec = Assert.Single(results);
        Assert.False(rec.Passed);
        var check = Assert.Single(rec.Checks, c => c.CheckName == "工具调用");
        Assert.False(check.Passed);
        Assert.Contains("乙", check.Reason);
        Assert.Contains("甲", check.Reason); // 实际调用列表也列出
    }

    [Fact]
    public void Tools_no_calls_reports_none()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", tools: ["甲"]) });
        var results = Run(recorder, new EvalItem("q", "纯文本回复，没调工具"));

        var check = Assert.Single(Assert.Single(results).Checks, c => c.CheckName == "工具调用");
        Assert.False(check.Passed);
        Assert.Contains("无", check.Reason);
    }

    [Fact]
    public void ExtractCalledTools_reads_function_call_contents()
    {
        var item = ItemWithToolCalls("A", "B");
        var called = EvalCaseRecorder.ExtractCalledTools(item);

        Assert.Equal(2, called.Count);
        Assert.Contains("A", called);
        Assert.Contains("B", called);
    }

    // —— 非空（最小长度）——

    [Theory]
    [InlineData("足够长的回复内容", true)]
    [InlineData("短", false)]
    public void MinLength_decides_verdict(string response, bool expectedPass)
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", minLength: 5) });
        var results = Run(recorder, new EvalItem("q", response));

        Assert.Equal(expectedPass, Assert.Single(results).Passed);
    }

    // —— 未配置断言：不记录检查，case 恒通过（仅采集回复）——

    [Fact]
    public void Unconfigured_checks_produce_no_records_but_case_passes()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q") });
        var results = Run(recorder, new EvalItem("q", "采集到的回复"));

        var rec = Assert.Single(results);
        Assert.True(rec.Passed);
        Assert.Empty(rec.Checks);
        Assert.Equal("采集到的回复", rec.Response);
    }

    [Fact]
    public void Case_passed_requires_all_configured_checks()
    {
        var recorder = new EvalCaseRecorder(new[] { Case("q", keywords: ["甲"], minLength: 100) });
        var results = Run(recorder, new EvalItem("q", "包含甲但不够长"));

        var rec = Assert.Single(results);
        Assert.False(rec.Passed);
        Assert.Equal(2, rec.Checks.Count); // 只记录已配置的 2 项（关键词+非空），未配置的 2 项零噪音
        Assert.Contains(rec.Checks, c => c.CheckName == "关键词" && c.Passed);
        Assert.Contains(rec.Checks, c => c.CheckName == "非空" && !c.Passed);
    }

    // —— 顺序与共享 ——

    [Fact]
    public void GetResults_preserves_case_order()
    {
        var recorder = new EvalCaseRecorder(new[]
        {
            Case("第一"), Case("第二"), Case("第三"),
        });
        // 检查按乱序 item 触发，结果仍按 case 声明顺序
        var results = Run(recorder,
            new EvalItem("第三", "3"), new EvalItem("第一", "1"), new EvalItem("第二", "2"));

        Assert.Equal(3, results.Count);
        Assert.Equal("第一", results[0].Query);
        Assert.Equal("第二", results[1].Query);
        Assert.Equal("第三", results[2].Query);
    }

    [Fact]
    public void Duplicate_query_shares_first_case_record()
    {
        var recorder = new EvalCaseRecorder(new[]
        {
            Case("重复", keywords: ["甲"]), Case("重复", minLength: 999),
        });
        var results = Run(recorder, new EvalItem("重复", "包含甲"));

        // 首条配置生效：第二条的 minLength 被忽略，两条 case 共享同一条判定记录
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed));
        Assert.All(results, r => Assert.Single(r.Checks, c => c.CheckName == "关键词"));
    }
}
