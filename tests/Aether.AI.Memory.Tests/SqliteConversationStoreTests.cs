using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory.Stores;

namespace Aether.AI.Memory.Tests;

public class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationStore _store;

    public SqliteConversationStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conv-test-{Guid.NewGuid():N}.db");
        _store = SqliteConversationStore.ForFile(_dbPath);
    }

    [Fact]
    public async Task Save_Get_conversation_round_trip()
    {
        var conv = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "你好",
        };

        await _store.SaveConversationAsync(conv);

        var loaded = await _store.GetConversationAsync(conv.ConversationId);
        Assert.NotNull(loaded);
        Assert.Equal("Tester", loaded!.AgentName);
        Assert.Equal("agent-1", loaded.AgentId);
        Assert.Equal("你好", loaded.Title);
    }

    [Fact]
    public async Task AppendRun_persists_run_and_events_and_updates_preview()
    {
        var conv = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "t",
        };
        await _store.SaveConversationAsync(conv);

        var run = new AgentRun
        {
            ConversationId = conv.ConversationId,
            Seq = 1,
            Status = RunStatus.Completed,
            AgentId = conv.AgentId,
            AgentName = conv.AgentName,
            UserMessage = "hi",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var events = new List<AgentEvent>
        {
            new()
            {
                RunId = run.Id,
                Seq = 1,
                EventType = AgentEventType.TextChunk,
                Payload = AgentEventPayload.Serialize(new AgentEventPayload.TextChunk("hello there")),
            },
        };

        await _store.AppendRunAsync(run, events);

        // 读回 run + events
        var runs = await _store.GetRunsAsync(conv.ConversationId);
        Assert.Single(runs);
        Assert.Equal("hi", runs[0].UserMessage);

        var loadedEvents = await _store.GetEventsAsync(runs[0].Id);
        Assert.Single(loadedEvents);
        Assert.Equal(AgentEventType.TextChunk, loadedEvents[0].EventType);
        var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(loadedEvents[0].Payload);
        Assert.Equal("hello there", text?.Text);

        // preview/updatedAt 反范式化更新
        var loadedConv = await _store.GetConversationAsync(conv.ConversationId);
        Assert.NotNull(loadedConv);
        Assert.Equal("hello there", loadedConv!.LastMessagePreview);
        Assert.NotNull(loadedConv.LastMessageAt);
    }

    [Fact]
    public async Task GetSummaries_orders_by_updated_desc_and_includes_preview()
    {
        var old = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "old" };
        await _store.SaveConversationAsync(old);
        await AppendTextRunAsync(old.ConversationId, 1, "old message", startedAt: DateTimeOffset.UtcNow.AddHours(-1));

        var recent = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "recent" };
        await _store.SaveConversationAsync(recent);
        await AppendTextRunAsync(recent.ConversationId, 1, "recent message", startedAt: DateTimeOffset.UtcNow);

        var summaries = await _store.GetSummariesAsync("");
        Assert.Equal(2, summaries.Count);
        Assert.Equal(recent.ConversationId, summaries[0].ConversationId); // newer first
        Assert.Equal("recent message", summaries[0].LastMessagePreview);
        Assert.Equal("old message", summaries[1].LastMessagePreview);
    }

    [Fact]
    public async Task Delete_removes_conversation_runs_and_events()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "t" };
        await _store.SaveConversationAsync(conv);
        await AppendTextRunAsync(conv.ConversationId, 1, "x");

        Assert.True(await _store.DeleteAsync(conv.ConversationId));
        Assert.Null(await _store.GetConversationAsync(conv.ConversationId));
        Assert.Empty(await _store.GetRunsAsync(conv.ConversationId));
        Assert.Empty(await _store.GetSummariesAsync(""));
    }

    [Fact]
    public async Task Runs_ordered_by_seq_and_events_ordered_by_seq()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "t" };
        await _store.SaveConversationAsync(conv);

        // 故意乱序 append（seq 2 先、seq 1 后），读回应按 seq 升序。
        await AppendTextRunAsync(conv.ConversationId, 2, "second");
        await AppendTextRunAsync(conv.ConversationId, 1, "first");

        var runs = await _store.GetRunsAsync(conv.ConversationId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(1, runs[0].Seq);
        Assert.Equal("first", runs[0].UserMessage);
        Assert.Equal(2, runs[1].Seq);
        Assert.Equal("second", runs[1].UserMessage);
    }

    private async Task AppendTextRunAsync(Guid conversationId, int seq, string text, DateTimeOffset? startedAt = null)
    {
        var at = startedAt ?? DateTimeOffset.UtcNow;
        var run = new AgentRun
        {
            ConversationId = conversationId,
            Seq = seq,
            Status = RunStatus.Completed,
            AgentId = "a",
            AgentName = "A",
            UserMessage = text,
            StartedAt = at,
            FinishedAt = at,
        };
        var events = new List<AgentEvent>
        {
            new()
            {
                RunId = run.Id,
                Seq = 1,
                EventType = AgentEventType.TextChunk,
                Payload = AgentEventPayload.Serialize(new AgentEventPayload.TextChunk(text)),
            },
        };
        await _store.AppendRunAsync(run, events);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
    }
}
