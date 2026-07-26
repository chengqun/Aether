using Aether.AI.Core.Conversations;
using Aether.AI.Core.Messages;
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
    public async Task Save_Get_round_trip_with_messages()
    {
        var conv = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "你好",
        };
        conv.Append(Message.User("hi"));
        conv.Append(Message.Assistant("hello there"));

        await _store.SaveAsync(conv);

        var loaded = await _store.GetAsync(conv.ConversationId);
        Assert.NotNull(loaded);
        Assert.Equal("Tester", loaded!.AgentName);
        Assert.Equal("agent-1", loaded.AgentId);
        Assert.Equal("你好", loaded.Title);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(MessageRole.User, loaded.Messages[0].Role);
        Assert.Equal("hi", loaded.Messages[0].Content);
        Assert.Equal("hello there", loaded.Messages[1].Content);
    }

    [Fact]
    public async Task Save_replaces_messages_on_update()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), AgentId = "a", AgentName = "A", Title = "t" };
        conv.Append(Message.User("first"));
        await _store.SaveAsync(conv);

        conv.Append(Message.Assistant("reply"));
        conv.Title = "t2";
        await _store.SaveAsync(conv);

        var loaded = await _store.GetAsync(conv.ConversationId);
        Assert.NotNull(loaded);
        Assert.Equal("t2", loaded!.Title);
        Assert.Equal(2, loaded.Messages.Count); // not duplicated
    }

    [Fact]
    public async Task GetSummaries_orders_by_updated_desc_and_includes_preview()
    {
        var old = new Conversation { ConversationId = Guid.NewGuid(), AgentId = "a", AgentName = "A", Title = "old" };
        old.Append(Message.User("old message"));
        old.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await _store.SaveAsync(old);

        var recent = new Conversation { ConversationId = Guid.NewGuid(), AgentId = "a", AgentName = "A", Title = "recent" };
        recent.Append(Message.User("recent message"));
        await _store.SaveAsync(recent);

        var summaries = await _store.GetSummariesAsync("");
        Assert.Equal(2, summaries.Count);
        Assert.Equal(recent.ConversationId, summaries[0].ConversationId); // newer first
        Assert.Equal("recent message", summaries[0].LastMessagePreview);
        Assert.Equal("old message", summaries[1].LastMessagePreview);
    }

    [Fact]
    public async Task Delete_removes_conversation_and_messages()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), AgentId = "a", AgentName = "A", Title = "t" };
        conv.Append(Message.User("x"));
        await _store.SaveAsync(conv);

        Assert.True(await _store.DeleteAsync(conv.ConversationId));
        Assert.Null(await _store.GetAsync(conv.ConversationId));
        Assert.Empty(await _store.GetSummariesAsync(""));
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
    }
}
