using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Moments.Domain;

namespace Acme.Mobile.Shared.Services;

// Stage 2 服务桩：实时推送 + 离线缓存 + 登录后同步编排的 no-op 实现。
// Stage 1 仅跑 Splash→Login→agent聊天，这些服务不被该路径消费；注册桩保证若有 VM 偶发解析不抛。
// Stage 2 替换为真实实现（SignalRChatService / SqliteStoreBase 缓存 / sync 编排）。

public sealed class NoOpSignalRChatService : ISignalRChatService
{
    public bool IsConnected => false;
    public event Func<ChatMessage, Task>? OnMessageReceived { add { } remove { } }
    public event Func<Guid, Guid, int, Task>? OnUnreadCountUpdated { add { } remove { } }
    public event Func<Task>? OnReconnected { add { } remove { } }
    public event Action<Guid>? OnNewConversation { add { } remove { } }
    public Task ConnectAsync(string baseUrl) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task JoinConversationAsync(Guid conversationId) => Task.CompletedTask;
}

public sealed class NoOpMessageCacheService : IMessageCacheService
{
    public Task<List<ChatMessage>> GetCachedMessagesAsync(Guid conversationId, int? limit = null) => Task.FromResult(new List<ChatMessage>());
    public Task<DateTimeOffset?> GetLastCachedAtAsync(Guid conversationId) => Task.FromResult<DateTimeOffset?>(null);
    public Task CacheMessagesAsync(Guid conversationId, List<ChatMessage> messages, int? maxCacheSize = null) => Task.CompletedTask;
    public Task AppendMessageAsync(Guid conversationId, ChatMessage message) => Task.CompletedTask;
    public Task ClearCacheAsync(Guid conversationId) => Task.CompletedTask;
}

public sealed class NoOpConversationSyncService : IConversationSyncService
{
    public Task<List<Conversation>> GetCachedConversationsAsync(Guid userId) => Task.FromResult(new List<Conversation>());
    public Task<List<Conversation>> SyncFromServerAsync(Guid userId) => Task.FromResult(new List<Conversation>());
    public Task UpsertConversationAsync(Conversation conversation, Guid userId) => Task.CompletedTask;
    public Task DeleteConversationAsync(Guid conversationId) => Task.CompletedTask;
}

public sealed class NoOpContactSyncService : IContactSyncService
{
    public Task<List<Relationship>> GetCachedContactsAsync(Guid userId) => Task.FromResult(new List<Relationship>());
    public Task<List<Relationship>> SyncFromServerAsync(Guid userId) => Task.FromResult(new List<Relationship>());
}

public sealed class NoOpMomentCacheService : IMomentCacheService
{
    public Task<List<Moment>> GetCachedMomentsAsync() => Task.FromResult(new List<Moment>());
    public Task<List<Moment>> GetCachedMomentsAsync(string targetId) => Task.FromResult(new List<Moment>());
    public Task<DateTimeOffset?> GetLatestCachedCreatedAtAsync() => Task.FromResult<DateTimeOffset?>(null);
    public Task UpdateCacheAsync(List<Moment> moments) => Task.CompletedTask;
    public Task AddMomentAsync(Moment moment) => Task.CompletedTask;
    public Task ClearCacheAsync() => Task.CompletedTask;
}

public sealed class NoOpCoreInitializer : ICoreInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;
}

public sealed class NoOpPostLoginInitializer : IPostLoginInitializer
{
    public Task<PostLoginResult> InitializeCriticalAsync() => Task.FromResult(new PostLoginResult { AgentConfigLoaded = true });
    public Task StartBackgroundSyncsAsync() => Task.CompletedTask;
}
