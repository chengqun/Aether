using Acme.ChatSocial.Domain;
using Acme.Moments.Domain;

namespace Acme.Mobile.Mvvm.Services;

#pragma warning disable CS1591 // mobile contracts are internal-facing

/// <summary>
/// 社交聊天 SignalR 实时推送服务（手机端独有；桌面端去掉了）。
/// 连接服务端 <c>/hubs/chat</c> hub，鉴权用 <c>IAppTokenStore</c> 持有的 access token。
/// 实现：移植自 AlphaAgent <c>SignalRChatService</c>，落 Mobile.Shared（Stage 1）。
/// </summary>
public interface ISignalRChatService
{
    bool IsConnected { get; }

    event Func<ChatMessage, Task>? OnMessageReceived;
    event Func<Guid, Guid, int, Task>? OnUnreadCountUpdated;
    event Func<Task>? OnReconnected;
    event Action<Guid>? OnNewConversation;

    Task ConnectAsync(string baseUrl);
    Task DisconnectAsync();
    Task JoinConversationAsync(Guid conversationId);

    /// <summary>查询某台桌面电脑的在线状态 + 当前选中专家名（只读）。deviceId = 32 位设备字符串。
    /// 返回 null = 未连接/查询失败。供桌面助手页只读状态显示。</summary>
    Task<DeviceStatusResult?> GetDeviceStatusAsync(string deviceId);
}

/// <summary>桌面只读状态（供桌面助手页显示）。</summary>
public sealed class DeviceStatusResult
{
    public bool IsOnline { get; set; }
    public string SelectedAgentName { get; set; } = string.Empty;
}

/// <summary>消息本地缓存（离线优先）。实现：SqliteStoreBase（Stage 1）。</summary>
public interface IMessageCacheService
{
    Task<List<ChatMessage>> GetCachedMessagesAsync(Guid conversationId, int? limit = null);
    Task<DateTimeOffset?> GetLastCachedAtAsync(Guid conversationId);
    Task CacheMessagesAsync(Guid conversationId, List<ChatMessage> messages, int? maxCacheSize = null);
    Task AppendMessageAsync(Guid conversationId, ChatMessage message);
    Task ClearCacheAsync(Guid conversationId);
}

/// <summary>社交会话列表本地缓存 + 服务端同步。</summary>
public interface IConversationSyncService
{
    Task<List<Conversation>> GetCachedConversationsAsync(Guid userId);
    Task<List<Conversation>> SyncFromServerAsync(Guid userId);
    Task UpsertConversationAsync(Conversation conversation, Guid userId);
    Task DeleteConversationAsync(Guid conversationId);
}

/// <summary>通讯录本地缓存 + 服务端同步。返回扁平 Relationship 列表（按 Type 分类）。</summary>
public interface IContactSyncService
{
    Task<List<Relationship>> GetCachedContactsAsync(Guid userId);
    Task<List<Relationship>> SyncFromServerAsync(Guid userId);
}

/// <summary>朋友圈本地缓存 + 服务端同步。</summary>
public interface IMomentCacheService
{
    Task<List<Moment>> GetCachedMomentsAsync();
    Task<List<Moment>> GetCachedMomentsAsync(string targetId);
    Task<DateTimeOffset?> GetLatestCachedCreatedAtAsync();
    Task UpdateCacheAsync(List<Moment> moments);
    Task AddMomentAsync(Moment moment);
    Task ClearCacheAsync();
}

/// <summary>核心初始化（DB schema ensure 等），启动时调用一次。</summary>
public interface ICoreInitializer
{
    Task InitializeAsync();
}

/// <summary>
/// 登录后初始化（三段式：Tier1 关键阻塞本地配置 / Tier2 后台同步 / Tier3 惰性按需）。
/// 实现：Mobile.Shared（Stage 1），编排各缓存/sync 服务。
/// </summary>
public interface IPostLoginInitializer
{
    Task<PostLoginResult> InitializeCriticalAsync();
    Task StartBackgroundSyncsAsync();
}

public class PostLoginResult
{
    public bool AgentConfigLoaded { get; set; }
}

/// <summary>
/// LLM 配置变更后重新应用到运行时（让新保存/同步的默认 LLM 对 agent 立即生效，无需重登）。
/// 镜像桌面 <c>MainViewModelBase.RefreshLlmConfigsAsync</c>。实现：Mobile.Shared（Stage 1）。
/// </summary>
public interface IAgentConfigInitializer
{
    Task RefreshLlmConfigAsync();
}

// —— Qifu（毓数 / 云文档）认证：本地手机接口。
//   Acme.Qifu.* 业务项目隐藏于 slnx（不同步 GitHub），故手机 VM 层不直接引用它们；
//   由 Qifu.Mobile 租户宿主提供适配器实现，桥接到真实的 Acme.Qifu.* 服务。
//   行为镜像桌面端 AiSettings（浏览器 OAuth 桌面 / 服务器 token 同步 手机）。

/// <summary>毓数平台认证服务（手机端从服务器同步 token，桌面端浏览器 OAuth）。</summary>
public interface IYushuAuthService
{
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);
    /// <returns>登录成功返回非空 token；手机端=从服务器同步到的 token，桌面端=OAuth token。</returns>
    Task<string?> LoginAsync(CancellationToken ct = default);
}

/// <summary>毓数 token 与服务器双向同步。</summary>
public interface IYushuTokenSyncService
{
    Task<bool> SyncToServerAsync(CancellationToken ct = default);
    Task<bool> SyncFromServerAsync(CancellationToken ct = default);
}

/// <summary>云文档认证结果（桌面端 Playwright SSO 捕获 sid）。</summary>
public sealed class CloudDocsLoginResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>云文档认证服务（桌面端 Playwright SSO / 手机端从服务器拉 sid）。</summary>
public interface ICloudDocsAuthService
{
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);
    Task<CloudDocsLoginResult?> LoginAsync(CancellationToken ct = default);
}

/// <summary>云文档 sid 与服务器双向同步。</summary>
public interface ICloudDocsTokenSyncService
{
    Task<bool> SyncToServerAsync(CancellationToken ct = default);
    Task<bool> SyncFromServerAsync(CancellationToken ct = default);
}

// —— mcphub 网关（毓数 SQL 查询/权限工单的鉴权）认证：本地手机接口。
//   与毓数/云文档同模式：Mvvm 层定义本地接口，Qifu.Mobile 宿主桥接到 Domain 层 IMcphubAuthService。
//   mcphub 手机端只需「从服务器同步 JWT」（上传是桌面端连接器的活），故不设 Mvvm token-sync 接口。

/// <summary>mcphub 网关认证服务（手机端从服务器同步 JWT，桌面端浏览器 OAuth）。</summary>
public interface IMcphubAuthService
{
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);
    /// <returns>登录成功返回非空 JWT；手机端=从服务器同步到的 JWT，桌面端=浏览器 OAuth JWT。</returns>
    Task<string?> LoginAsync(CancellationToken ct = default);
}
