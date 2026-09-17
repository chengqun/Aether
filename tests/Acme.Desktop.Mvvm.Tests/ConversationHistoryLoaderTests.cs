using Acme.Desktop.Mvvm;
using Aether.AI.Core.Conversations;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// ConversationHistoryLoader（ChatVM 拆分 B4）单测：typed 事件流 → transcript 重建 round-trip
/// （用 ChatTranscriptPersister 落库再加载，天然锁定「持久化形状 == 重建输入形状」）、NextRunSeq 计算、null 路径。
/// </summary>
public class ConversationHistoryLoaderTests
{
    private static (ConversationHistoryLoader Loader, ChatTranscriptPersister Persister, InMemoryConversationStore Store) Create()
    {
        var store = new InMemoryConversationStore();
        return (new ConversationHistoryLoader(store), new ChatTranscriptPersister(store), store);
    }

    private static async Task<Conversation> SeedConversationAsync(IConversationStore store, string agentName = "tester")
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), AgentId = Guid.NewGuid().ToString(), AgentName = agentName };
        await store.SaveConversationAsync(conv);
        return conv;
    }

    private static ChatMessageItem Text(string content, bool reasoning = false) => new()
    {
        Side = ChatMessageSide.Left, ItemType = "text", Sender = "agent",
        Content = content, IsReasoning = reasoning, IsReasoningContent = reasoning,
        IsStreaming = false,
    };

    private static ChatMessageItem Tool(string callId, string name) => new()
    {
        Side = ChatMessageSide.Left, ItemType = "tool", Sender = "agent",
        ToolName = name, CallId = callId, Input = new Dictionary<string, object>(),
        IsToolCompleted = true, Output = new Dictionary<string, object>(),
    };

    [Fact]
    public async Task LoadAsync_RoundTrips_Persisted_Run_Into_Turns()
    {
        var (loader, persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId);

        await persister.PersistRunAsync(runtime, "user question", new List<ChatMessageItem>
        {
            Text("thinking...", reasoning: true),
            Tool("call-1", "grep"),
            Text("the answer"),
        });

        var result = await loader.LoadAsync(conv.ConversationId, conv.AgentName);

        Assert.NotNull(result);
        var turn = Assert.Single(result!.Turns); // 每条 run 一轮
        Assert.Equal(2, result.NextRunSeq);      // 1 条 run（seq=1）→ 下一个 2
        // Header=run.UserMessage + 用户气泡为 Children 首项（TurnRebuilder 语义），agent 产出随后
        Assert.Equal("user question", turn.Header);
        Assert.Equal(4, turn.Children.Count); // 用户气泡 + reasoning + 工具 + 答案
        Assert.Contains(turn.Children, i => i.IsUser && i.Content == "user question");
        Assert.Contains(turn.Children, i => i.IsText && i.IsReasoning && i.Content == "thinking...");
        Assert.Contains(turn.Children, i => i.IsToolStep && i.ToolName == "grep" && i.IsToolCompleted);
        Assert.Contains(turn.Children, i => i.IsText && !i.IsReasoning && i.Content == "the answer");
    }

    [Fact]
    public async Task LoadAsync_Computes_NextRunSeq_From_Max_Seq()
    {
        var (loader, persister, store) = Create();
        var conv = await SeedConversationAsync(store);
        var runtime = new ConversationRuntime(conv.ConversationId) { NextRunSeq = 7 };

        await persister.PersistRunAsync(runtime, "q1", new List<ChatMessageItem> { Text("a1") });
        await persister.PersistRunAsync(runtime, "q2", new List<ChatMessageItem> { Text("a2") });

        var result = await loader.LoadAsync(conv.ConversationId, null);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Turns.Count); // 两轮
        Assert.Equal(9, result.NextRunSeq);   // runs seq 7,8 → max+1 = 9
    }

    [Fact]
    public async Task LoadAsync_Conversation_Without_Runs_Gives_Empty_Turns_And_Seq_1()
    {
        var (loader, _, store) = Create();
        var conv = await SeedConversationAsync(store);

        var result = await loader.LoadAsync(conv.ConversationId, "tester");

        Assert.NotNull(result);
        Assert.Empty(result!.Turns);
        Assert.Equal(1, result.NextRunSeq);
    }

    [Fact]
    public async Task LoadAsync_Missing_Conversation_Returns_Null()
    {
        var (loader, _, _) = Create();

        var result = await loader.LoadAsync(Guid.NewGuid(), "tester");

        Assert.Null(result); // VM 据此把 NextRunSeq 归 1
    }
}
