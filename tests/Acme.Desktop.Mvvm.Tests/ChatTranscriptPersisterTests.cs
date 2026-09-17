using Acme.Desktop.Mvvm;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// ChatTranscriptPersister（ChatVM 拆分 B1）单测：run+events 事件流映射语义锁定——
/// PlanUpdated / ToolCall+ToolResult 按 CallId 配对 / ReasoningChunk / TextChunk / 跳过项 / seq 取号 / 状态判定 / null 路径。
/// </summary>
public class ChatTranscriptPersisterTests
{
    private static (ChatTranscriptPersister Persister, InMemoryConversationStore Store) Create()
    {
        var store = new InMemoryConversationStore();
        return (new ChatTranscriptPersister(store), store);
    }

    private static async Task<Conversation> SeedConversationAsync(IConversationStore store)
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), AgentId = Guid.NewGuid().ToString(), AgentName = "tester" };
        await store.SaveConversationAsync(conv);
        return conv;
    }

    private static ChatMessageItem Text(string content, bool reasoning = false) => new()
    {
        Side = ChatMessageSide.Left, ItemType = "text", Sender = "agent",
        Content = content, IsReasoning = reasoning, IsReasoningContent = reasoning,
        IsStreaming = false,
    };

    private static ChatMessageItem Tool(string callId, string name, Dictionary<string, object>? output = null) => new()
    {
        Side = ChatMessageSide.Left, ItemType = "tool", Sender = "agent",
        ToolName = name, CallId = callId, Input = new Dictionary<string, object>(),
        IsToolCompleted = output is not null, Output = output,
    };

    private static ChatMessageItem PlanCard() => new()
    {
        Side = ChatMessageSide.Left, ItemType = "plan", Sender = "agent", IsStreaming = false,
    };

    [Fact]
    public async Task Persists_Text_Reasoning_Tool_Plan_Events_In_Order()
    {
        var (persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId);

        var plan = PlanCard();
        plan.Todos.Add(new TodoItemVM { Title = "step1", IsComplete = false });

        var runItems = new List<ChatMessageItem>
        {
            Text("thinking...", reasoning: true),
            Tool("call-1", "grep", new Dictionary<string, object> { ["hits"] = 3 }),
            Text("answer text"),
        };

        var result = await persister.PersistRunAsync(runtime, "user question", runItems);

        Assert.NotNull(result);
        Assert.Equal(conv.ConversationId, result.ConversationId);
        Assert.Equal(1, result.RunSeq);

        var runs = await store.GetRunsAsync(conv.ConversationId);
        var run = Assert.Single(runs);
        Assert.Equal("user question", run.UserMessage);
        Assert.Equal("tester", run.AgentName);
        Assert.Equal(RunStatus.Completed, run.Status); // 有非空答案文本

        var events = await store.GetEventsAsync(run.Id);
        Assert.Equal(4, events.Count); // ReasoningChunk + ToolCall + ToolResult + TextChunk（ToolCall/Result 合一卡两事件）
        Assert.Equal(AgentEventType.ReasoningChunk, events[0].EventType);
        Assert.Equal(AgentEventType.ToolCall, events[1].EventType);
        Assert.Equal(AgentEventType.ToolResult, events[2].EventType);
        Assert.Equal(AgentEventType.TextChunk, events[3].EventType);

        // 事件 seq 从 1 连续递增
        Assert.Equal(1, events[0].Seq);
        Assert.Equal(2, events[1].Seq);
        Assert.Equal(3, events[2].Seq);
        Assert.Equal(4, events[3].Seq);

        // CallId 在 ToolCall/ToolResult 写入时即配对
        var call = AgentEventPayload.Deserialize<AgentEventPayload.ToolCall>(events[1].Payload);
        var toolResult = AgentEventPayload.Deserialize<AgentEventPayload.ToolResult>(events[2].Payload);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("call-1", toolResult.CallId);
        Assert.NotNull(toolResult.Output);
        // Dictionary<string,object> 经 JSON 序列化 round-trip 后叶子值是 JsonElement（盒装）——按字符串比较
        Assert.Equal("3", toolResult.Output["hits"].ToString());
    }

    [Fact]
    public async Task Plan_Card_Maps_To_PlanUpdated_With_Todos()
    {
        var (persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId);

        var plan = PlanCard();
        plan.Todos.Add(new TodoItemVM { Title = "step1", Description = "d1", IsComplete = true });
        plan.Todos.Add(new TodoItemVM { Title = "step2", IsComplete = false });

        var result = await persister.PersistRunAsync(runtime, "q", new List<ChatMessageItem> { plan });

        Assert.NotNull(result);
        var run = Assert.Single(await store.GetRunsAsync(conv.ConversationId));
        var events = await store.GetEventsAsync(run.Id);
        var planEvent = Assert.Single(events);
        Assert.Equal(AgentEventType.PlanUpdated, planEvent.EventType);
        var payload = AgentEventPayload.Deserialize<AgentEventPayload.PlanUpdated>(planEvent.Payload);
        Assert.Equal(2, payload.Todos.Count);
        Assert.Equal("step1", payload.Todos[0].Title);
        Assert.True(payload.Todos[0].IsComplete);
        Assert.False(payload.Todos[1].IsComplete);
    }

    [Fact]
    public async Task Uncompleted_Tool_Step_Persists_Call_Without_Result()
    {
        var (persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId);

        // 中断的 run：只有 call，无 result，无答案文本
        var runItems = new List<ChatMessageItem> { Tool("call-9", "run_in_terminal") };

        var result = await persister.PersistRunAsync(runtime, "q", runItems);

        Assert.NotNull(result);
        var run = Assert.Single(await store.GetRunsAsync(conv.ConversationId));
        Assert.Equal(RunStatus.Interrupted, run.Status); // 无答案 → Interrupted
        var events = await store.GetEventsAsync(run.Id);
        var call = Assert.Single(events); // 只有 ToolCall，无 ToolResult
        Assert.Equal(AgentEventType.ToolCall, call.EventType);
    }

    [Fact]
    public async Task Skips_User_Approval_And_Empty_Reasoning_Items()
    {
        var (persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId);

        var runItems = new List<ChatMessageItem>
        {
            new() { Side = ChatMessageSide.Right, ItemType = "text", Sender = "我", Content = "user bubble" },
            new() { Side = ChatMessageSide.Left, ItemType = "approval", Sender = "agent", ToolName = "t", ApprovalState = 1 },
            Text("", reasoning: true), // 空思考段——跳过
            Text("real answer"),
        };

        var result = await persister.PersistRunAsync(runtime, "user message", runItems);

        Assert.NotNull(result);
        var run = Assert.Single(await store.GetRunsAsync(conv.ConversationId));
        var events = await store.GetEventsAsync(run.Id);
        var ev = Assert.Single(events);
        Assert.Equal(AgentEventType.TextChunk, ev.EventType);
        Assert.Equal("real answer", AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(ev.Payload).Text);
    }

    [Fact]
    public async Task Seq_Numbers_Increment_Per_Run_And_Across_Persists()
    {
        var (persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId) { NextRunSeq = 5 };

        await persister.PersistRunAsync(runtime, "q1", new List<ChatMessageItem> { Text("a1") });
        await persister.PersistRunAsync(runtime, "q2", new List<ChatMessageItem> { Text("a2") });

        var runs = (await store.GetRunsAsync(conv.ConversationId)).OrderBy(r => r.Seq).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(5, runs[0].Seq);  // Interlocked 取号：从 NextRunSeq 起（5 先于自增）
        Assert.Equal(6, runs[1].Seq);
        Assert.Equal(7, runtime.NextRunSeq);
    }

    [Fact]
    public async Task Returns_Null_For_Empty_Conversation_Id()
    {
        var (persister, _) = Create();
        var runtime = new ConversationRuntime(Guid.Empty);

        var result = await persister.PersistRunAsync(runtime, "q", new List<ChatMessageItem> { Text("a") });

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_Null_For_Missing_Conversation()
    {
        var (persister, _) = Create();
        var runtime = new ConversationRuntime(Guid.NewGuid()); // 未 SaveConversationAsync

        var result = await persister.PersistRunAsync(runtime, "q", new List<ChatMessageItem> { Text("a") });

        Assert.Null(result);
    }
}
