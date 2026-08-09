using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 手机端核心初始化（启动时调一次，Splash 期）。Aether 各 SQLite store 自带 lazy schema init
///（<c>EnsureSchemaAsync</c> 首调触发），此处显式调一次廉价读强制建表 + WAL，让 social-cache.db
/// 在登录前就绪（而非首次打开聊天页才建）。镜像 AlphaAgent <c>DatabaseInitializer.EnsureCreatedAsync</c>，
/// 但用 Aether 的 SqliteStoreBase 自初始化机制，无需 EF Core 迁移。
/// </summary>
public sealed class MobileCoreInitializer : ICoreInitializer
{
    private readonly IMessageCacheService? _messageCache;
    private readonly IConversationSyncService? _conversationSync;
    private readonly IContactSyncService? _contactSync;
    private readonly IMomentCacheService? _momentCache;

    public MobileCoreInitializer(
        IMessageCacheService? messageCache = null,
        IConversationSyncService? conversationSync = null,
        IContactSyncService? contactSync = null,
        IMomentCacheService? momentCache = null)
    {
        _messageCache = messageCache;
        _conversationSync = conversationSync;
        _contactSync = contactSync;
        _momentCache = momentCache;
    }

    public async Task InitializeAsync()
    {
        // 廉价读触发各 store 的 EnsureSchemaAsync（Interlocked 守卫，只建一次）。Guid.Empty 查询返回空，无副作用。
        try
        {
            if (_messageCache != null) await _messageCache.GetCachedMessagesAsync(Guid.Empty).ConfigureAwait(false);
            if (_conversationSync != null) await _conversationSync.GetCachedConversationsAsync(Guid.Empty).ConfigureAwait(false);
            if (_contactSync != null) await _contactSync.GetCachedContactsAsync(Guid.Empty).ConfigureAwait(false);
            if (_momentCache != null) await _momentCache.GetCachedMomentsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CoreInit] 缓存 schema 初始化异常（不阻塞启动）: {ex.Message}");
        }
    }
}
