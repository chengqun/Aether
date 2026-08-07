using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Scripting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 代码规则大脑（<see cref="CodeRuleBrain"/>）测试：验证「C# 源码 → Roslyn 编译 → 代码大脑」管线，
/// 及代码大脑经 <see cref="RuleChatClient"/> + <c>AsHarnessAgent</c> 驱动 MAF agent 循环（工具执行 →
/// 结果回填 → 终答终止）。镜像 <see cref="RuleAgentTests"/> 的端到端刺探，区别在大脑是 C# 代码而非 YAML 规则程序。
/// </summary>
public class CodeRuleBrainTests
{
    // —— Roslyn 编译 ——

    [Fact]
    public async Task RuleScriptCompiler_trycompile_success()
    {
        var source = @"
using System.Collections.Generic;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;

public class CompiledEcho : CodeRuleBrain
{
    protected override IReadOnlyList<AIContent> Decide()
        => Reply(""hi "" + UserText);
}";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.True(ok, error ?? "(no error)");
        Assert.NotNull(brain);
        Assert.Null(error);

        // 编译产物是 CodeRuleBrain 子类——喂上下文验证它确实按源码逻辑回 Reply。
        var ctx = new RuleContext { LatestUser = new ChatMessage(ChatRole.User, "world") };
        var output = await brain!.DecideAsync(ctx, default);
        Assert.Equal("hi world", Assert.Single(output.OfType<TextContent>()).Text);
    }

    [Fact]
    public void RuleScriptCompiler_trycompile_error()
    {
        // 缺分号 + 未闭合大括号 → 编译诊断
        var source = @"
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;

public class Broken : CodeRuleBrain
{
    protected override IReadOnlyList<AIContent> Decide()
        return Reply(""x"")
}";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RuleScriptCompiler_trycompile_no_brain_subtype()
    {
        // 合法 C# 但无 CodeRuleBrain 子类 → 错误
        var source = "public class Plain { }";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // —— 代码大脑驱动 MAF agent 循环 ——

    [Fact]
    public async Task CodeRuleBrain_drives_agent_loop()
    {
        // 用户消息原样调 Echo → 工具回填后用 LastResult 终答。
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(new ToolThenReplyBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-spike",
            Name = "CodeRuleSpike",
            Description = "code rule agent spike",
            // 规则大脑不消费注入式 NL 上下文——关 TodoProvider，否则它追加 todo user 消息污染 LatestUser。
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        bool sawResult = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("600000", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent) sawResult = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.True(sawResult, "MAF 应执行 Echo 并回填 FunctionResultContent");
        Assert.Equal("结果：echo:600000", finalText);
    }

    [Fact]
    public async Task CodeRuleBrain_is_stateful_across_steps()
    {
        // step0 把「UserText + "_step0"」存进字段 _captured（证明实例在轮内复用，字段跨 Decide 存活）；
        // step1 用 _captured（字段，非可从 LatestUser 推导）+ LastResult（工具回填）终答。
        // 终答必含 "_step0"——只有字段持久化才可能；并含 "echo:"——证明 LastResult 回填。
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(new StatefulBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-stateful",
            Name = "CodeRuleStateful",
            Description = "stateful code rule",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("abc", session))
        {
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // 字段持久化：含 "_step0"；LastResult 回填：含 "echo:abc"。
        Assert.Contains("abc_step0", finalText);
        Assert.Contains("echo:abc", finalText);
    }

    private static string Echo(string text) => $"echo:{text}";

    /// <summary>step0 调 Echo(UserText)；step1 用 LastResult 终答。</summary>
    private sealed class ToolThenReplyBrain : CodeRuleBrain
    {
        protected override IReadOnlyList<AIContent> Decide()
        {
            if (Step == 0) return Call("Echo", new { text = UserText });
            return Reply("结果：" + LastResult);
        }
    }

    /// <summary>step0 存字段 + 调 Echo；step1 用字段 + LastResult 终答（证明状态多步链）。</summary>
    private sealed class StatefulBrain : CodeRuleBrain
    {
        private string? _captured;
        protected override IReadOnlyList<AIContent> Decide()
        {
            if (Step == 0)
            {
                _captured = UserText + "_step0"; // 仅 step0 写入；step1 读不到则证明实例未复用
                return Call("Echo", new { text = UserText });
            }
            return Reply((_captured ?? "?") + "|" + LastResult);
        }
    }
}
