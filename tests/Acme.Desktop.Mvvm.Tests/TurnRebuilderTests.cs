using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations.Runs;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// <see cref="TurnRebuilder"/>（typed 事件流 → transcript turns 纯函数）的行为测试。
/// 锁定从 <see cref="ChatWorkspaceViewModel"/> 抽出时保持的原语义：user 气泡 / text 与 reasoning 还原 /
/// 工具 call+result 按 CallId 合并成单卡 / plan 卡 / run 状态映射 / 多 run 按 Seq 排序。
/// </summary>
public class TurnRebuilderTests
{
    private static AgentRun Run(int seq, RunStatus status, string userMessage, string agentName = "专家")
        => new()
        {
            Id = Guid.NewGuid(),
            Seq = seq,
            Status = status,
            AgentName = agentName,
            UserMessage = userMessage,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow.AddSeconds(2),
        };

    private static AgentEvent Ev(Guid runId, int seq, AgentEventType type, object payload)
        => new() { RunId = runId, Seq = seq, EventType = type, Payload = AgentEventPayload.Serialize(payload) };

    [Fact]
    public void SingleRun_ProducesTurnWithUserBubbleFirst()
    {
        var run = Run(1, RunStatus.Completed, "你好");
        var turns = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>>(), "专家");

        Assert.Single(turns);
        Assert.Equal("你好", turns[0].Header);
        Assert.Equal(TurnStatus.Done, turns[0].Status);
        // 第一子项 = 用户气泡（右侧）
        Assert.True(turns[0].Children[0].IsUser);
        Assert.Equal("你好", turns[0].Children[0].Content);
    }

    [Fact]
    public void TextAndReasoning_MapToDistinctSegments()
    {
        var run = Run(1, RunStatus.Completed, "q", "专家");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ReasoningChunk, new AgentEventPayload.ReasoningChunk("思考...")),
            Ev(run.Id, 2, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("答案是 42")),
        };

        var turn = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>> { [run.Id] = events }, "专家")[0];

        var reasoning = turn.Children.First(c => c.IsReasoningContent);
        var answer = turn.Children.First(c => c.IsText && !c.IsReasoningContent && !c.IsUser);
        Assert.Equal("思考...", reasoning.Content);
        Assert.True(reasoning.IsReasoning);
        Assert.Equal("答案是 42", answer.Content);
    }

    [Fact]
    public void ToolCallAndResult_MergeIntoOneCardByCallId()
    {
        var run = Run(1, RunStatus.Completed, "q");
        var callId = "call-123";
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall(callId, "git_status", new Dictionary<string, object>())),
            Ev(run.Id, 2, AgentEventType.ToolResult, new AgentEventPayload.ToolResult(callId, "git_status", new Dictionary<string, object> { ["result"] = "clean" })),
        };

        var turn = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>> { [run.Id] = events }, "专家")[0];

        var toolCards = turn.Children.Where(c => c.IsToolStep).ToList();
        Assert.Single(toolCards); // call+result 合并成一张卡，非两张
        Assert.Equal("git_status", toolCards[0].ToolName);
        Assert.True(toolCards[0].IsToolCompleted);
    }

    [Fact]
    public void InterruptedRun_OrphanToolCall_StillRenderedAsCard()
    {
        var run = Run(1, RunStatus.Interrupted, "q");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("c1", "run_tests", null)),
            // 无 result——中断的孤儿 call
        };

        var turn = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>> { [run.Id] = events }, "专家")[0];

        var card = turn.Children.Single(c => c.IsToolStep);
        Assert.False(card.IsToolCompleted); // 无 result → 未完成
    }

    [Fact]
    public void PlanUpdated_MapsToPlanCardWithTodos()
    {
        var run = Run(1, RunStatus.Completed, "q");
        var todos = new[] { new TodoItemInfo("t1", "写代码", "desc", false), new TodoItemInfo("t2", "跑测试", null, true) };
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.PlanUpdated, new AgentEventPayload.PlanUpdated(todos)),
        };

        var turn = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>> { [run.Id] = events }, "专家")[0];

        var plan = turn.Children.Single(c => c.IsPlanCard);
        Assert.Equal(2, plan.Todos.Count);
        Assert.Equal("写代码", plan.Todos[0].Title);
        Assert.True(plan.Todos[1].IsComplete);
    }

    [Fact]
    public void FailedRun_MapsToErrorTurn()
    {
        var run = Run(1, RunStatus.Failed, "q");
        var turns = TurnRebuilder.BuildTurnsFromRuns(new[] { run }, new Dictionary<Guid, IReadOnlyList<AgentEvent>>(), "专家");
        Assert.Equal(TurnStatus.Error, turns[0].Status);
    }

    [Fact]
    public void MultipleRuns_SortedBySeq()
    {
        var r1 = Run(2, RunStatus.Completed, "第二条");
        var r2 = Run(1, RunStatus.Completed, "第一条");
        var turns = TurnRebuilder.BuildTurnsFromRuns(new[] { r1, r2 }, new Dictionary<Guid, IReadOnlyList<AgentEvent>>(), "专家");

        Assert.Equal(2, turns.Count);
        Assert.Equal("第一条", turns[0].Header);
        Assert.Equal("第二条", turns[1].Header);
    }
}
