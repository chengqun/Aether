using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Notes.Domain.Agents;
using Aether.AI.Memory;
using Acme.Mobile.Mvvm.Services;
using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using Acme.Notes.Domain;
using Acme.AppAuth.Application.Auth;
using Acme.ServiceAccount.Domain;
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ChatViewModel : NavigableViewModelBase
{
    private readonly IChatService? _chatService;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IConversationStore? _conversationStore;
    private readonly IAppAuthService? _authService;
    private readonly ISignalRChatService? _signalRChatService;
    private readonly IEventBusService? _eventBusService;
    private readonly IUnreadMessageCacheService? _unreadMessageCacheService;
    private readonly IGlobalMessageHandler? _globalMessageHandler;
    private readonly IConversationSyncService? _conversationSyncService;
    private readonly ICurrentUser? _currentUser;
    private readonly IServiceAccountService? _serviceAccountService;
    private readonly IAppSettings? _appSettings;
    private readonly IPostLoginInitializer? _postLoginInitializer;
    private readonly IUpdateService? _updateService;
    private readonly IPlatformInfo? _platformInfo;
    private bool _isLoaded = false;
    private Guid? _cachedUserId;
    private bool _isSubscribed = false;
    private DateTime _lastSyncTime = DateTime.MinValue;
    private static readonly TimeSpan _minSyncInterval = TimeSpan.FromSeconds(30);

    [ObservableProperty]
    private string _title = "聊天";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableConversation? _selectedConversation;

    public ObservableCollection<ObservableConversation> Conversations { get; } = new();

    /// <summary>
    /// 导航到聊天详情页事件
    /// </summary>
    public event EventHandler<ChatDetailNavigationEventArgs>? NavigateToChatDetailRequested;

    /// <summary>
    /// 导航到 Agent 聊天详情页事件
    /// </summary>
    public event EventHandler<AgentChatDetailNavigationEventArgs>? NavigateToAgentChatDetailRequested;

    /// <summary>
    /// 导航到服务号信息流事件
    /// </summary>
    public event EventHandler? NavigateToServiceAccountFeedRequested;

    /// <summary>
    /// 后台检查到强制更新时触发,由 ChatPage 弹窗拦截(强制更新不导航,等待用户安装新版)。
    /// </summary>
    public event EventHandler<CheckUpdateResult>? ForceUpdateRequired;

    public ChatViewModel(IChatService? chatService = null,
                         ICustomAgentCatalog? agentCatalog = null,
                         IConversationStore? conversationStore = null,
                         IAppAuthService? authService = null,
                         ISignalRChatService? signalRChatService = null,
                         IEventBusService? eventBusService = null,
                         IUnreadMessageCacheService? unreadMessageCacheService = null,
                         IGlobalMessageHandler? globalMessageHandler = null,
                         IConversationSyncService? conversationSyncService = null,
                         ICurrentUser? currentUser = null,
                         IServiceAccountService? serviceAccountService = null,
                         IAppSettings? appSettings = null,
                         IUIThreadDispatcher? uiThreadDispatcher = null,
                         IPostLoginInitializer? postLoginInitializer = null,
                         IUpdateService? updateService = null,
                         IPlatformInfo? platformInfo = null)
    {
        _chatService = chatService;
        _agentCatalog = agentCatalog;
        _conversationStore = conversationStore;
        _authService = authService;
        _signalRChatService = signalRChatService;
        _eventBusService = eventBusService;
        _unreadMessageCacheService = unreadMessageCacheService;
        _globalMessageHandler = globalMessageHandler;
        _conversationSyncService = conversationSyncService;
        _currentUser = currentUser;
        _serviceAccountService = serviceAccountService;
        _appSettings = appSettings;
        _uiThreadDispatcher = uiThreadDispatcher;
        _postLoginInitializer = postLoginInitializer;
        _updateService = updateService;
        _platformInfo = platformInfo;
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        if (_cachedUserId.HasValue)
            return _cachedUserId.Value;

        if (_currentUser != null)
        {
            var raw = await _currentUser.GetCurrentUserIdAsync();
            if (Guid.TryParse(raw, out var uid))
            {
                _cachedUserId = uid;
                return uid;
            }
        }

        _cachedUserId = new Guid("11111111-1111-1111-1111-111111111111");
        return _cachedUserId.Value;
    }

    public override async Task OnActivatedAsync()
    {
        _globalMessageHandler?.StartListening();
        SubscribeSignalREvents();

        if (!_isLoaded)
        {
            // 首次加载：从本地缓存瞬间显示会话
            await LoadFromCacheAsync();
            await LoadServiceAccountFolderAsync();
            _isLoaded = true;
            // 首次进聊天页：触发 Tier 2 延迟后台同步（股票主数据，fire-and-forget 不阻塞 UI）。
            // 见 IPostLoginInitializer 三段式契约；LLM/Agent/Yushu/CloudDocs 同步按需在各自用点触发。
            _ = Task.Run(() => _postLoginInitializer?.StartBackgroundSyncsAsync());
            // 后台检查更新（仅强制更新才弹窗拦截，非强制不打扰）
            _ = Task.Run(CheckUpdateAsync);
            _ = SyncInBackgroundAsync();
        }
        else if (ShouldBackgroundSync())
        {
            // 返回页面且距上次同步超过阈值：后台同步
            _ = SyncInBackgroundAsync();
        }

        // SignalR 后台连接（非阻塞：连不上不卡 UI；每次激活都尝试，保留断线重连）
        _ = EnsureSignalRConnectedAsync();
        LoadCachedUnreadMessages();
    }

    public override async Task OnDeactivatedAsync()
    {
        // 不取消 EventBus 订阅——保持实时事件接收
    }

    /// <summary>
    /// 后台检查更新:仅检测到强制更新时,在 UI 线程触发 <see cref="ForceUpdateRequired"/>
    /// 由 ChatPage 弹窗拦截(强制更新不导航,等待用户安装新版);非强制更新不打扰。
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        if (_updateService == null) return;

        try
        {
            var platform = _platformInfo?.GetAppPlatform() ?? AppPlatform.Android;
            var versionCode = _platformInfo?.GetVersionCode() ?? 1;

            var result = await Task.Run(() => _updateService.CheckUpdateAsync(platform, versionCode));
            if (result != null && result.HasUpdate && result.IsForce)
            {
                _uiThreadDispatcher?.Post(() => ForceUpdateRequired?.Invoke(this, result));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 检查更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从本地 SQLite 缓存加载会话列表（瞬间完成，无转圈）
    /// </summary>
    private async Task LoadFromCacheAsync()
    {
        try
        {
            var (userId, cachedConversations) = await Task.Run(async () =>
            {
                var uid = await GetCurrentUserIdAsync();
                var cached = _conversationSyncService != null
                    ? await _conversationSyncService.GetCachedConversationsAsync(uid)
                    : new List<Conversation>();
                return (uid, cached);
            });

            // 构建 Agent 名称 → Kind 映射（用于补充缓存中缺失的 Kind）
            var agentKindMap = await BuildAgentKindMapAsync();

            Conversations.Clear();

            foreach (var conv in cachedConversations)
            {
                // AI 会话（Type 3/4）已移至「AI」tab，不进社交列表（过滤历史残留缓存条目）。
                if (conv.Type == 3 || conv.Type == 4) continue;
                // 设备会话已迁移至「桌面助手」页，不进社交列表。
                if (IsDeviceConversation(conv)) continue;

                // 为缓存中的 Agent 会话补充 AgentName 和 Kind 信息
                if (conv.Type == 3 || conv.Type == 4)
                {
                    // 如果 AgentName 为空，尝试从 DisplayName 解析
                    if (string.IsNullOrEmpty(conv.AgentName) && !string.IsNullOrEmpty(conv.Name))
                    {
                        if (conv.Type == 3)
                        {
                            // Type 3: DisplayName 就是 AgentName
                            conv.AgentName = conv.Name;
                        }
                        else if (conv.Type == 4 && conv.Name.Contains(" · "))
                        {
                            // Type 4: DisplayName 格式是 "股票名 · AgentName"
                            var parts = conv.Name.Split(" · ", 2);
                            if (parts.Length == 2)
                            {
                                conv.AgentName = parts[1];
                            }
                        }
                    }

                    // 如果 Kind 为空，从 AgentName 映射
                    if (conv.Kind == null && !string.IsNullOrEmpty(conv.AgentName))
                    {
                        conv.Kind = (int?)agentKindMap.GetValueOrDefault(conv.AgentName);
                    }

                    // 如果补充了信息，更新数据库
                    if (!string.IsNullOrEmpty(conv.AgentName) && conv.Kind != null && _conversationSyncService != null)
                    {
                        try
                        {
                            await _conversationSyncService.UpsertConversationAsync(conv, userId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 更新缓存失败: {ex.Message}");
                        }
                    }

                    // 诊断：确认 Agent 会话的 Kind 已设置
                    Console.WriteLine($"[ChatVM] Conv: {conv.Name ?? conv.OtherUserName ?? "?"}, AgentName={conv.AgentName}, Kind={conv.Kind}, IsAgentKindAgent={conv.Kind == (int)AgentKind.Agent}");
                }
                Conversations.Add(new ObservableConversation(conv));
            }

            SortConversations();
            StatusMessage = Conversations.Count == 0 ? "暂无会话" : string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 加载缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 构建 Agent 名称 → Kind 映射（基于 <see cref="ICustomAgentCatalog"/> 的 <see cref="CustomAgentDefinition.NodeType"/>）。
    /// </summary>
    private async Task<Dictionary<string, AgentKind>> BuildAgentKindMapAsync()
    {
        var map = new Dictionary<string, AgentKind>();
        try
        {
            if (_agentCatalog != null)
            {
                var agents = await _agentCatalog.ListAsync();
                foreach (var a in agents)
                {
                    map[a.Name] = a.NodeType switch
                    {
                        NoteNodeType.Workflow => AgentKind.Workflow,
                        NoteNodeType.Rule or NoteNodeType.CodeRule => AgentKind.RuleBased,
                        _ => AgentKind.Agent,
                    };
                }
            }
        }
        catch { /* 获取 Agent 信息失败时返回空映射 */ }
        return map;
    }

    /// <summary>
    /// 后台同步服务端会话，更新本地缓存和 UI（不阻塞 UI）
    /// </summary>
    private async Task SyncInBackgroundAsync()
    {
        if (_conversationSyncService == null) return;

        try
        {
            var userId = await GetCurrentUserIdAsync();
            var serverConversations = await Task.Run(() => _conversationSyncService.SyncFromServerAsync(userId));

            // 预先构建 Kind 映射，供 UI 线程回调使用
            var agentKindMap = await BuildAgentKindMapAsync();

            _uiThreadDispatcher?.Post(() =>
            {
                foreach (var serverConv in serverConversations)
                {
                    // 为 Agent 会话补充 Kind
                    if ((serverConv.Type == 3 || serverConv.Type == 4) && serverConv.Kind == null && !string.IsNullOrEmpty(serverConv.AgentName))
                    {
                        serverConv.Kind = (int?)agentKindMap.GetValueOrDefault(serverConv.AgentName);
                    }

                    // 设备会话已迁移至「桌面助手」页，不进社交列表。
                    if (IsDeviceConversation(serverConv)) continue;

                    var local = Conversations.FirstOrDefault(c => c.Id == serverConv.Id);
                    if (local != null)
                    {
                        local.UpdateFromSource(serverConv);
                    }
                    else
                    {
                        Conversations.Add(new ObservableConversation(serverConv));
                    }
                }

                // 移除服务端已不存在、或已转为设备会话的会话（排除 Agent 会话和服务号文件夹）。
                var serverIds = serverConversations.Select(c => c.Id).ToHashSet();
                for (int i = Conversations.Count - 1; i >= 0; i--)
                {
                    var conv = Conversations[i];
                    if (conv.Type != 3 && conv.Type != 4 && conv.Type != 5
                        && (!serverIds.Contains(conv.Id) || !string.IsNullOrEmpty(conv.OtherDeviceId)))
                    {
                        Conversations.RemoveAt(i);
                    }
                }

                SortConversations();
                _lastSyncTime = DateTime.UtcNow;

                if (Conversations.Count == 0)
                    StatusMessage = "暂无会话";
                else
                    StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 后台同步失败: {ex.Message}");
        }
    }

    private bool ShouldBackgroundSync()
    {
        return DateTime.UtcNow - _lastSyncTime > _minSyncInterval;
    }

    private void LoadCachedUnreadMessages()
    {
        if (_unreadMessageCacheService == null) return;

        bool hasCachedMessages = false;
        foreach (var conv in Conversations)
        {
            if (_unreadMessageCacheService.HasCachedMessages(conv.Id))
            {
                var cached = _unreadMessageCacheService.GetAndClearCache(conv.Id);
                if (cached.Any())
                {
                    hasCachedMessages = true;
                    if (cached.Last().Content != null)
                    {
                        conv.LastMessage = cached.Last().Content;
                        conv.LastMessageTime = cached.Last().CreationTime;
                    }
                }
            }
        }

        if (hasCachedMessages)
            SortConversations();
    }

    private void SubscribeSignalREvents()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;

        _eventBusService?.Subscribe<NewMessageEvent>(OnNewMessage);
        _eventBusService?.Subscribe<UnreadCountUpdatedEvent>(OnUnreadCountUpdated);
        _eventBusService?.Subscribe<ConversationReadEvent>(OnConversationRead);
        _eventBusService?.Subscribe<NewConversationEvent>(OnNewConversation);
        _eventBusService?.Subscribe<NewConversationFromServerEvent>(OnNewConversationFromServer);
        _eventBusService?.Subscribe<SignalRReconnectedEvent>(OnSignalRReconnected);
        _globalMessageHandler?.StartListeningReconnected();
    }

    private async Task EnsureSignalRConnectedAsync()
    {
        if (_signalRChatService == null) return;
        if (_signalRChatService.IsConnected) return;

        try
        {
            var serverAddress = _appSettings?.ServerBaseAddress ?? "https://localhost/";
            await Task.Run(() => _signalRChatService.ConnectAsync(serverAddress));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] SignalR 连接失败: {ex.Message}");
        }
    }

    private void UnsubscribeSignalREvents()
    {
        if (!_isSubscribed) return;
        _isSubscribed = false;

        _eventBusService?.Unsubscribe<NewMessageEvent>(OnNewMessage);
        _eventBusService?.Unsubscribe<UnreadCountUpdatedEvent>(OnUnreadCountUpdated);
        _eventBusService?.Unsubscribe<ConversationReadEvent>(OnConversationRead);
        _eventBusService?.Unsubscribe<NewConversationEvent>(OnNewConversation);
        _eventBusService?.Unsubscribe<NewConversationFromServerEvent>(OnNewConversationFromServer);
        _eventBusService?.Unsubscribe<SignalRReconnectedEvent>(OnSignalRReconnected);
    }

    private async void OnSignalRReconnected(SignalRReconnectedEvent @event)
    {
        System.Diagnostics.Debug.WriteLine("[ChatViewModel] SignalR 重连，后台同步会话列表");
        _ = SyncInBackgroundAsync();
    }

    private async void OnNewMessage(NewMessageEvent @event)
    {
        // Progress 心跳不动会话列表预览/未读（不当 LastMessage、不 UnreadCount++）。
        if (string.Equals(@event.Message.MessageType, "Progress", StringComparison.OrdinalIgnoreCase))
            return;

        // 自己发送的消息不增加未读数
        var currentUserId = await GetCurrentUserIdAsync();
        var isFromSelf = @event.Message.SenderId == currentUserId;

        _uiThreadDispatcher?.Post(() =>
        {
            var conv = Conversations.FirstOrDefault(c => c.Id == @event.Message.ConversationId);
            if (conv != null)
            {
                if (!isFromSelf)
                    conv.UnreadCount++;
                conv.LastMessage = @event.Message.Content;
                conv.LastMessageTime = @event.Message.CreationTime;
                SortConversations();
            }
            else if (!isFromSelf)
            {
                // 收到未知会话的消息 — 创建占位条目，并立即从服务端同步完整数据替换
                var placeholder = new Conversation
                {
                    Id = @event.Message.ConversationId,
                    Type = 0,
                    OtherUserName = @event.Message.SenderName,
                    UnreadCount = 1,
                    LastMessage = @event.Message.Content,
                    LastMessageTime = @event.Message.CreationTime
                };
                Conversations.Add(new ObservableConversation(placeholder));
                SortConversations();

                _ = SavePlaceholderToCacheAsync(placeholder);
                _ = SyncSingleConversationAsync(@event.Message.ConversationId);
            }
        });
    }

    private async Task SavePlaceholderToCacheAsync(Conversation placeholder)
    {
        if (_conversationSyncService == null) return;
        try
        {
            var userId = await GetCurrentUserIdAsync();
            await Task.Run(() => _conversationSyncService.UpsertConversationAsync(placeholder, userId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 保存占位会话失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从服务端同步单个会话的完整数据，替换占位会话。
    /// 用于收到已删除会话的新消息时，自动恢复会话并显示完整信息。
    /// </summary>
    private async Task SyncSingleConversationAsync(Guid conversationId)
    {
        if (_chatService == null || _conversationSyncService == null) return;

        try
        {
            var userId = await GetCurrentUserIdAsync();
            var conversations = await Task.Run(() => _chatService.GetMyConversationsAsync());
            if (conversations == null || conversations.Count == 0) return;

            var match = conversations.FirstOrDefault(c => c.Id == conversationId);
            if (match == null) return; // 服务端也没有该会话，忽略
            if (IsDeviceConversation(match)) return; // 设备会话已迁移至「桌面助手」页，不进社交列表

            _uiThreadDispatcher?.Post(() =>
            {
                var existing = Conversations.FirstOrDefault(c => c.Id == conversationId);
                if (existing != null)
                {
                    // 替换占位会话为完整数据
                    var index = Conversations.IndexOf(existing);
                    Conversations[index] = new ObservableConversation(match);
                }
                else
                {
                    Conversations.Add(new ObservableConversation(match));
                }
                SortConversations();
            });

            // 同步到本地缓存
            await Task.Run(() => _conversationSyncService.UpsertConversationAsync(match, userId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 同步单个会话失败: {ex.Message}");
        }
    }

    private async void OnUnreadCountUpdated(UnreadCountUpdatedEvent @event)
    {
        // 只处理当前用户的未读计数更新（SignalR 广播给整个组）
        var currentUserId = await GetCurrentUserIdAsync();
        if (@event.UserId != currentUserId) return;

        _uiThreadDispatcher?.Post(() =>
        {
            var conv = Conversations.FirstOrDefault(c => c.Id == @event.ConversationId);
            if (conv != null)
            {
                conv.UnreadCount = @event.UnreadCount;
            }
        });
    }

    private void OnConversationRead(ConversationReadEvent @event)
    {
        _uiThreadDispatcher?.Post(() =>
        {
            var conv = Conversations.FirstOrDefault(c => c.Id == @event.ConversationId);
            if (conv != null)
            {
                conv.UnreadCount = 0;
            }
        });
    }

    private async void OnNewConversation(NewConversationEvent @event)
    {
        var conv = @event.Conversation;

        // AI 会话（Type 3/4）由 AiChatViewModel 接收，不进社交列表。
        if (conv.Type == 3 || conv.Type == 4) return;
        // 设备会话已迁移至「桌面助手」页，不进社交列表。
        if (IsDeviceConversation(conv)) return;

        // 对于 Agent 会话（Type 3/4），确保 Kind 属性已设置
        if ((conv.Type == 3 || conv.Type == 4) && conv.Kind == null && !string.IsNullOrEmpty(conv.AgentName))
        {
            // 构建 Agent 名称 → Kind 映射
            var agentKindMap = await BuildAgentKindMapAsync();
            conv.Kind = (int?)agentKindMap.GetValueOrDefault(conv.AgentName);
            
            // 调试日志
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] OnNewConversation: AgentName={conv.AgentName}, Kind={conv.Kind}");
        }

        // 保存到本地缓存（包括 Agent 会话，确保 APP 重启后仍可见）
        if (_conversationSyncService != null)
        {
            try
            {
                var userId = await GetCurrentUserIdAsync();
                await Task.Run(() => _conversationSyncService.UpsertConversationAsync(conv, userId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 缓存新会话失败: {ex.Message}");
            }
        }

        // 更新 UI（所有类型都添加到列表）
        _uiThreadDispatcher?.Post(() =>
        {
            var existing = Conversations.FirstOrDefault(c => c.Id == conv.Id);
            if (existing == null)
            {
                Conversations.Add(new ObservableConversation(conv));
                SortConversations();
                if (StatusMessage == "暂无会话")
                    StatusMessage = string.Empty;
            }
        });
    }

    /// <summary>
    /// 处理服务端通知的新会话：自动加入 SignalR 组 + 同步完整会话数据。
    /// 适用场景：通讯录好友发来第一条消息、对方删除会话后再发消息恢复。
    /// </summary>
    private async void OnNewConversationFromServer(NewConversationFromServerEvent @event)
    {
        var conversationId = @event.ConversationId;

        // 自动加入 SignalR 会话组，确保后续消息能实时接收
        if (_signalRChatService != null)
        {
            try
            {
                await Task.Run(() => _signalRChatService.JoinConversationAsync(conversationId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 加入新会话组失败: {ex.Message}");
            }
        }

        // 从服务端同步该会话的完整数据，替换占位会话（如果存在）或新增
        _ = SyncSingleConversationAsync(conversationId);
    }

    /// <summary>
    /// 设备会话（<see cref="Conversation.OtherDeviceId"/> 非空）已迁移至「AI 页 → 桌面助手」，
    /// 不再出现在社交聊天列表——此处统一过滤（底层数据保留）。
    /// </summary>
    private static bool IsDeviceConversation(Conversation conv) =>
        !string.IsNullOrEmpty(conv.OtherDeviceId);

    /// <summary>
    /// 排序会话列表：只在顺序变化时才重建，减少 CollectionView 闪烁
    /// </summary>
    private void SortConversations()
    {
        var desired = Conversations.OrderByDescending(c => c.LastMessageTime).ToList();

        bool needsReorder = false;
        for (int i = 0; i < desired.Count; i++)
        {
            if (Conversations[i].Id != desired[i].Id)
            {
                needsReorder = true;
                break;
            }
        }

        if (!needsReorder) return;

        Conversations.Clear();
        foreach (var conv in desired)
        {
            Conversations.Add(conv);
        }
    }

    /// <summary>
    /// 加载服务号信息流文件夹条目（Type=5），显示在聊天列表中
    /// </summary>
    private async Task LoadServiceAccountFolderAsync()
    {
        if (_serviceAccountService == null) return;

        try
        {
            var result = await Task.Run(() => _serviceAccountService.GetFollowedPostsAsync());
            if (result == null || !result.Any()) return;

            var latestPost = result.First();
            var folderConversation = new Conversation
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000005"), // 固定 ID，代表服务号文件夹
                Type = 5,
                Name = "服务号",
                LastMessage = latestPost.Title,
                LastMessageTime = latestPost.PublishedAt
            };

            // 避免重复添加
            if (!Conversations.Any(c => c.Type == 5))
            {
                Conversations.Add(new ObservableConversation(folderConversation));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] 加载服务号文件夹失败: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RefreshConversationsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "刷新中...";

            if (_conversationSyncService != null)
            {
                var userId = await GetCurrentUserIdAsync();
                var allConversations = await Task.Run(() => _conversationSyncService.SyncFromServerAsync(userId));

                Conversations.Clear();
                foreach (var conv in allConversations)
                {
                    // AI 会话（Type 3/4）已移至「AI」tab，不进社交列表。
                    if (conv.Type == 3 || conv.Type == 4) continue;
                    // 设备会话已迁移至「桌面助手」页，不进社交列表。
                    if (IsDeviceConversation(conv)) continue;
                    Conversations.Add(new ObservableConversation(conv));
                }
            }
            else
            {
                if (_chatService != null)
                {
                    var response = await Task.Run(() => _chatService.GetMyConversationsAsync());
                    if (response != null)
                    {
                        Conversations.Clear();
                        foreach (var conv in response)
                        {
                            // 设备会话已迁移至「桌面助手」页，不进社交列表。
                            if (IsDeviceConversation(conv)) continue;
                            Conversations.Add(new ObservableConversation(conv));
                        }
                    }
                    // response==null（HTTP 失败）→ 保留现有列表，不清空（避免失败导致会话列表变空）
                }
            }

            SortConversations();

            StatusMessage = Conversations.Count == 0 ? "暂无会话" : string.Empty;
            _isLoaded = true;
            _lastSyncTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task NavigateToChatDetailAsync()
    {
        if (SelectedConversation == null) return;

        var conv = SelectedConversation;
        SelectedConversation = null;

        if (conv.Type == 5)
        {
            // 服务号信息流
            NavigateToServiceAccountFeedRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (conv.Type == 4)
        {
            // 从 Context 解析 stockId 和 stockName（Context = "stock:{stockId}:{stockName}"）
            var stockName = "股票";
            var stockId = conv.Id.ToString();
            if (!string.IsNullOrEmpty(conv.Context) && conv.Context.StartsWith("stock:"))
            {
                var parts = conv.Context.Split(':', 3);
                if (parts.Length >= 2) stockId = parts[1];
                if (parts.Length >= 3) stockName = parts[2];
            }
            NavigateToAgentChatDetailRequested?.Invoke(this, new AgentChatDetailNavigationEventArgs
            {
                SessionId = conv.Id,
                StockId = stockId,
                StockName = stockName,
                AgentName = conv.AgentName ?? "指标分析Agent"
            });
            return;
        }

        if (conv.Type == 3)
        {
            NavigateToAgentChatDetailRequested?.Invoke(this, new AgentChatDetailNavigationEventArgs
            {
                SessionId = conv.Id,
                AgentName = conv.Name ?? "股票分析助手"
            });
            return;
        }

        var name = conv.Type == 0
            ? conv.OtherUserName ?? "未知用户"
            : conv.Name ?? "群聊";

        NavigateToChatDetailRequested?.Invoke(this, new ChatDetailNavigationEventArgs
        {
            ConversationId = conv.Id,
            ConversationName = name,
            ConversationType = conv.Type
        });
    }

    [RelayCommand]
    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var conv = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (conv == null) return;

        try
        {
            IsLoading = true;

            if (conv.Type == 3 || conv.Type == 4)
            {
                if (_conversationStore != null)
                {
                    await _conversationStore.DeleteAsync(conversationId);
                }

                // 同步删除本地缓存，防止下拉刷新时残留条目重新出现
                if (_conversationSyncService != null)
                {
                    await Task.Run(() => _conversationSyncService.DeleteConversationAsync(conversationId));
                }
            }
            else if (_chatService != null)
            {
                await Task.Run(() => _chatService.DeleteConversationAsync(conversationId));

                // 同步删除本地缓存
                if (_conversationSyncService != null)
                {
                    await Task.Run(() => _conversationSyncService.DeleteConversationAsync(conversationId));
                }
            }

            Conversations.Remove(conv);
            StatusMessage = Conversations.Count == 0 ? "暂无会话" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
/// 聊天详情导航参数
/// </summary>
public class ChatDetailNavigationEventArgs : EventArgs
{
    public Guid ConversationId { get; set; }
    public string ConversationName { get; set; } = "聊天";
    public int ConversationType { get; set; }
}

/// <summary>
/// Agent 聊天详情导航参数
/// </summary>
public class AgentChatDetailNavigationEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public string? StockId { get; set; }
    public string? StockName { get; set; }
    public string AgentName { get; set; } = "股票分析助手";
}
