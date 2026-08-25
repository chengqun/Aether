using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.AI.Core.Agents;
using Acme.ChatSocial.Domain;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ChatDetailViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService? _authService;
    private readonly IChatService? _chatService;
    private readonly ISignalRChatService? _signalRChatService;
    private readonly IMessageCacheService? _messageCacheService;
    private readonly IEventBusService? _eventBusService;
    private readonly ICurrentUser? _currentUser;
    private readonly IUnreadMessageCacheService? _unreadMessageCacheService;
    private readonly IClipboardService? _clipboardService;
    private readonly IToastService? _toastService;
    private readonly ISpeechRecognitionService? _speechRecognitionService;
    private Guid _currentConversationId;
    private Guid _currentUserId;

    // 语音识别取消令牌
    private CancellationTokenSource? _voiceCts;

    // 每次切换会话递增，用于丢弃过期的异步操作结果
    private int _loadVersion;

    // 防止 OnAppearingAsync 并发执行
    private volatile bool _isAppearing;

    // 缓存 userId，避免每次打开聊天页都重新解析 JWT
    private static Guid? _cachedUserId;

    /// <summary>
    /// 当前用户 Id 的静态缓存（解析 JWT 后设置）。供 <c>HumanChatMessageTemplateSelector</c>
    /// 在 XAML 渲染时区分"我的"/"对方"消息气泡——<see cref="ChatMessage"/> 是域类型无 Role 字段，
    /// 由选择器比较 <see cref="ChatMessage.SenderId"/> 与此缓存判定。<see cref="OnActivatedAsync"/>
    /// 中先解析 userId（设此缓存）再加载消息，故渲染时已就绪。
    /// </summary>
    public static Guid CachedUserId => _cachedUserId ?? Guid.Empty;

    // 从通讯录导航时，会话尚未创建，需在 ChatDetail 内部创建
    [ObservableProperty]
    private string _pendingContactId = string.Empty;

    [ObservableProperty]
    private string _pendingContactType = string.Empty;

    [ObservableProperty]
    private string _conversationId = string.Empty;

    [ObservableProperty]
    private string _conversationName = "聊天";

    [ObservableProperty]
    private int _conversationType;

    [ObservableProperty]
    private string _messageInput = string.Empty;

    /// <summary>
    /// 输入框是否有文字（控制发送按钮可见性）
    /// </summary>
    [ObservableProperty]
    private bool _hasMessageInput;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// 是否正在语音识别中
    /// </summary>
    [ObservableProperty]
    private bool _isListening;

    /// <summary>
    /// 是否为语音输入模式（默认 true：居中麦克风按钮；false：文字输入模式）
    /// </summary>
    [ObservableProperty]
    private bool _isVoiceMode = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    private readonly HashSet<Guid> _displayedMessageIds = new();
    private DateTime _lastMessageTime;

    public ChatDetailViewModel(IAppAuthService? authService = null,
                               IChatService? chatService = null,
                               ISignalRChatService? signalRChatService = null,
                               IMessageCacheService? messageCacheService = null,
                               IEventBusService? eventBusService = null,
                               ICurrentUser? currentUser = null,
                               IUnreadMessageCacheService? unreadMessageCacheService = null,
                               IUIThreadDispatcher? uiThreadDispatcher = null,
                               IClipboardService? clipboardService = null,
                               IToastService? toastService = null,
                               ISpeechRecognitionService? speechRecognitionService = null)
    {
        _authService = authService;
        _chatService = chatService;
        _signalRChatService = signalRChatService;
        _messageCacheService = messageCacheService;
        _eventBusService = eventBusService;
        _currentUser = currentUser;
        _unreadMessageCacheService = unreadMessageCacheService;
        _uiThreadDispatcher = uiThreadDispatcher;
        _clipboardService = clipboardService;
        _toastService = toastService;
        _speechRecognitionService = speechRecognitionService;
    }

    public override async Task OnActivatedAsync()
    {
        // 防止并发：如果上一次 OnAppearingAsync 还没跑完，先让它失效
        if (_isAppearing)
        {
            _loadVersion++; // 让旧操作的所有 await 后检查失败
        }
        _isAppearing = true;

        ResetState();
        var version = _loadVersion;

        if (_authService == null)
        {
            ErrorMessage = "服务未初始化";
            _isAppearing = false;
            return;
        }

        try
        {
            _currentUserId = await Task.Run(async () => await ResolveCurrentUserIdAsync());
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] OnAppearing: userId={_currentUserId}, PendingContactId={PendingContactId}, PendingContactType={PendingContactType}, ConversationId={ConversationId}");
            if (version != _loadVersion) { _isAppearing = false; return; }

            // 从通讯录导航：会话尚未创建，先创建再加载
            if (!string.IsNullOrEmpty(PendingContactId) && !string.IsNullOrEmpty(PendingContactType))
            {
                IsLoading = true;
                var conversationId = await CreatePendingConversationAsync();
                if (version != _loadVersion) { _isAppearing = false; return; }
                if (conversationId == null)
                {
                    IsLoading = false;
                    _isAppearing = false;
                    return;
                }
                _currentConversationId = conversationId.Value;
                ConversationId = conversationId.Value.ToString();
                System.Diagnostics.Debug.WriteLine($"[ChatDetail] 新建会话: convId={_currentConversationId}");
            }
            else if (!string.IsNullOrEmpty(ConversationId))
            {
                _currentConversationId = Guid.Parse(ConversationId);
                System.Diagnostics.Debug.WriteLine($"[ChatDetail] 已有会话: convId={_currentConversationId}");
            }
            else
            {
                ErrorMessage = "会话ID无效";
                _isAppearing = false;
                return;
            }

            // ★ 关键：_currentConversationId 已确定，立即订阅事件，确保不遗漏实时消息。
            // 之前放在 SyncFromServerAsync（fire-and-forget）中，经过多个 await 后才执行，
            // 导致设备/Agent 的快速回复在订阅前到达，消息被静默丢弃。
            SubscribeToEvents();

            // 1. 从本地 SQLite 加载历史消息，页面秒开
            await LoadCachedMessagesAsync(version);
            if (version != _loadVersion) { _isAppearing = false; return; }

            // 2. 从内存缓存加载 SignalR 已收到的新消息（避免闪一下）
            LoadInMemoryUnreadMessages();

            // 3. 后台同步：增量补漏 + 标记已读 + 加入 SignalR 组
            _ = SyncFromServerAsync();
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
                _isAppearing = false;
        }
    }

    public override async Task OnDeactivatedAsync()
    {
        UnsubscribeFromEvents();
    }

    private void ResetState()
    {
        _loadVersion++;
        Messages.Clear();
        _displayedMessageIds.Clear();
        _lastMessageTime = DateTime.MinValue;
        ErrorMessage = null;
        IsLoading = false;
    }

    /// <summary>
    /// MessageInput 变化时同步 HasMessageInput
    /// </summary>
    partial void OnMessageInputChanged(string value)
    {
        HasMessageInput = !string.IsNullOrWhiteSpace(value);
    }


    private async Task<Guid?> CreatePendingConversationAsync()
    {
        if (_chatService == null || string.IsNullOrEmpty(PendingContactId))
        {
            ErrorMessage = "服务未初始化或联系人ID无效";
            return null;
        }

        try
        {
            if (PendingContactType == "群组")
            {
                var groupId = Guid.Parse(PendingContactId);
                var groupConv = await Task.Run(() => _chatService.GetOrCreateGroupConversationAsync(groupId));
                if (groupConv == null)
                {
                    ErrorMessage = "创建群聊会话失败";
                    return null;
                }
                _eventBusService?.Publish(new NewConversationEvent(groupConv));
                return groupConv.Id;
            }
            else if (PendingContactType == "设备")
            {
                var deviceId = Guid.Parse(PendingContactId);
                System.Diagnostics.Debug.WriteLine($"[ChatDetail] 创建设备会话: PendingContactId={PendingContactId}, deviceId={deviceId}");
                var deviceConv = await Task.Run(() => _chatService.GetOrCreateDirectConversationAsync(deviceId));
                System.Diagnostics.Debug.WriteLine($"[ChatDetail] 设备会话响应: ConvId={deviceConv?.Id}");
                if (deviceConv == null)
                {
                    ErrorMessage = "创建设备会话失败";
                    return null;
                }
                _eventBusService?.Publish(new NewConversationEvent(deviceConv));
                return deviceConv.Id;
            }
            else
            {
                var targetUserId = Guid.Parse(PendingContactId);
                var directConv = await Task.Run(() => _chatService.GetOrCreateDirectConversationAsync(targetUserId));
                if (directConv == null)
                {
                    ErrorMessage = "创建会话失败";
                    return null;
                }
                _eventBusService?.Publish(new NewConversationEvent(directConv));
                return directConv.Id;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"创建会话失败: {ex.Message}";
            return null;
        }
    }

    private async Task<Guid> ResolveCurrentUserIdAsync()
    {
        if (_cachedUserId.HasValue) return _cachedUserId.Value;

        if (_currentUser == null) return Guid.Empty;

        var raw = await _currentUser.GetCurrentUserIdAsync();
        if (Guid.TryParse(raw, out var userId))
        {
            _cachedUserId = userId;
            return userId;
        }
        return Guid.Empty;
    }

    private const int MaxMessagesPerLoad = 20;

    /// <summary>
    /// 修正消息：将 CreationTime 从 UTC 转为本地时区。
    /// IsMine/ShowSenderName 由 XAML 经 Converter 根据 SenderId 与当前用户比较计算
    /// （Aether ChatMessage 无此字段，客户端绑定侧自行判断）。
    /// </summary>
    private ChatMessage FixMessage(ChatMessage msg)
    {
        // JSON 反序列化后 Kind 通常为 Unspecified，需先指定为 UTC 再转本地
        msg.CreationTime = msg.CreationTime.Kind == DateTimeKind.Local
            ? msg.CreationTime
            : DateTime.SpecifyKind(msg.CreationTime, DateTimeKind.Utc).ToLocalTime();
        return msg;
    }

    /// <summary>
    /// 从本地缓存加载消息，立即渲染
    /// </summary>
    private async Task LoadCachedMessagesAsync(int version)
    {
        if (_messageCacheService == null) return;

        var cached = await Task.Run(() => _messageCacheService.GetCachedMessagesAsync(_currentConversationId, MaxMessagesPerLoad));
        if (version != _loadVersion) return;
        System.Diagnostics.Debug.WriteLine($"[ChatDetail] 缓存消息: convId={_currentConversationId}, count={cached?.Count ?? 0}");
        if (cached == null || !cached.Any()) return;

        foreach (var msg in cached)
        {
            if (_displayedMessageIds.Add(msg.Id))
                Messages.Add(FixMessage(msg));
        }
        _lastMessageTime = Messages.LastOrDefault()?.CreationTime ?? DateTime.MinValue;
    }

    /// <summary>
    /// 从内存缓存取出 SignalR 已收到的新消息，立即渲染，避免"闪一下"
    /// </summary>
    private void LoadInMemoryUnreadMessages()
    {
        if (_unreadMessageCacheService == null) return;
        if (_currentConversationId == Guid.Empty) return;

        var cached = _unreadMessageCacheService.GetAndClearCache(_currentConversationId);
        if (cached == null || !cached.Any()) return;

        foreach (var msg in cached)
        {
            if (_displayedMessageIds.Add(msg.Id))
            {
                var fixedMsg = FixMessage(msg);
                Messages.Add(fixedMsg);
            }
        }
        _lastMessageTime = Messages.LastOrDefault()?.CreationTime ?? DateTime.MinValue;
    }

    /// <summary>
    /// 异步：加入 SignalR 会话组 + 按需网络加载 + 标记已读
    /// 事件订阅已在 OnActivatedAsync 中提前完成，此处不再重复订阅。
    /// </summary>
    private async Task SyncFromServerAsync()
    {
        var version = _loadVersion;
        var conversationId = _currentConversationId;
        try
        {
            // 1. 加入 SignalR 会话组，确保能收到该会话的实时消息
            IsConnected = _signalRChatService?.IsConnected ?? false;
            await JoinConversationGroupAsync(conversationId);

            // 2. 首次无任何缓存时显示转圈；有缓存时后台静默同步
            if (Messages.Count == 0)
                IsLoading = true;

            // 3. 增量补漏：从服务端拉取可能缺少的最新消息
            await LoadNetworkMessagesAsync(version, conversationId);
            if (version != _loadVersion) return;

            // 4. 标记已读 + 通知会话列表
            await MarkAsReadAndNotifyAsync(conversationId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 同步失败: {ex.Message}");
            // 即使网络同步失败，也确保事件已订阅
            if (version == _loadVersion)
                SubscribeToEvents();
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    /// <summary>
    /// REST 网络加载：增量插入新消息，避免 Clear+re-add 导致 UI 闪烁
    /// </summary>
    private async Task LoadNetworkMessagesAsync(int version, Guid conversationId)
    {
        if (_chatService == null) return;

        var response = await Task.Run(() => _chatService.GetMessagesAsync(conversationId, 0, MaxMessagesPerLoad));
        if (version != _loadVersion) return;
        System.Diagnostics.Debug.WriteLine($"[ChatDetail] 网络加载: convId={conversationId}, count={response?.Count ?? 0}");
        if (response == null)
        {
            System.Diagnostics.Debug.WriteLine("[ChatDetail] 网络加载失败: 返回 null");
            return;
        }

        var newMessages = response
            .Where(m => !string.Equals(m.MessageType, "Progress", StringComparison.OrdinalIgnoreCase))      // 历史不残留进度心跳
            .Where(m => !string.Equals(m.MessageType, "ApprovalDecision", StringComparison.OrdinalIgnoreCase)) // 历史不残留审批决策（控制消息）
            .Where(m => _displayedMessageIds.Add(m.Id))
            .Select(FixMessage)
            .OrderBy(m => m.CreationTime)
            .ToList();

        if (newMessages.Count == 0) return;

        // 增量插入：找到每条新消息的正确位置，避免 Clear+re-add
        foreach (var msg in newMessages)
        {
            var insertIndex = 0;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].CreationTime <= msg.CreationTime)
                {
                    insertIndex = i + 1;
                    break;
                }
            }
            Messages.Insert(insertIndex, msg);
        }

        _lastMessageTime = Messages.LastOrDefault()?.CreationTime ?? DateTime.MinValue;

        // 更新本地缓存
        if (_messageCacheService != null && Messages.Any())
        {
            await Task.Run(() => _messageCacheService.CacheMessagesAsync(conversationId, Messages.ToList(), MaxMessagesPerLoad));
        }
    }

    private async Task MarkAsReadAndNotifyAsync(Guid conversationId)
    {
        if (_chatService != null)
            await Task.Run(() => _chatService.MarkAsReadAsync(conversationId));

        _eventBusService?.Publish(new ConversationReadEvent(conversationId));
    }

    private void SubscribeToEvents()
    {
        // 先取消再订阅，防止重复注册导致同一事件触发多次处理
        UnsubscribeFromEvents();
        _eventBusService?.Subscribe<NewMessageEvent>(OnNewMessageEvent);
        _eventBusService?.Subscribe<SignalRReconnectedEvent>(OnSignalRReconnectedEvent);
        System.Diagnostics.Debug.WriteLine($"[ChatDetail] SubscribeToEvents: IsActive={IsActive}, ConversationId={_currentConversationId}, EventBus={_eventBusService != null}");
    }

    private void UnsubscribeFromEvents()
    {
        _eventBusService?.Unsubscribe<NewMessageEvent>(OnNewMessageEvent);
        _eventBusService?.Unsubscribe<SignalRReconnectedEvent>(OnSignalRReconnectedEvent);
    }

    private async void OnNewMessageEvent(NewMessageEvent @event)
    {
        System.Diagnostics.Debug.WriteLine($"[ChatDetail] OnNewMessageEvent: ConvId={@event.Message.ConversationId}, SenderId={@event.Message.SenderId}, CurrentConvId={_currentConversationId}, CurrentUserId={_currentUserId}");
        await HandleMessageForCurrentConversation(@event.Message);
    }

    private async void OnSignalRReconnectedEvent(SignalRReconnectedEvent @event)
    {
        await SyncMissedMessagesAsync();
    }

    /// <summary>
    /// 加入 SignalR 会话组，确保能收到该会话的实时消息
    /// </summary>
    private async Task JoinConversationGroupAsync(Guid conversationId)
    {
        if (_signalRChatService == null || conversationId == Guid.Empty) return;

        try
        {
            await Task.Run(() => _signalRChatService.JoinConversationAsync(conversationId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 加入会话组失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理当前会话的实时消息：跳过自己发送的回声，追加显示 + 标记已读 + 通知列表清零
    /// </summary>
    private async Task HandleMessageForCurrentConversation(ChatMessage message)
    {
        if (message.ConversationId != _currentConversationId)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: ConversationId 不匹配，跳过 (msg={message.ConversationId}, current={_currentConversationId})");
            return;
        }
        if (message.SenderId == _currentUserId)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: 自发自收，跳过 (SenderId={message.SenderId})");
            return;
        }

        var conversationId = _currentConversationId;
        var version = _loadVersion;

        System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: 显示消息 Id={message.Id}, SenderId={message.SenderId}, Content={message.Content?.Substring(0, Math.Min(message.Content?.Length ?? 0, 30))}");

        // Progress 心跳：富进度——解析 RunProgress JSON → ProgressChatMessage 按 kind 富渲染（与桌宠同款）。
        // 覆盖更新同 sender 的进度槽位（不堆积、不缓存、不标已读）。ChatMessage 是 POCO → 必须 Messages[idx]=pcm（Replace）触发刷新。
        var isProgress = string.Equals(message.MessageType, "Progress", StringComparison.OrdinalIgnoreCase);
        if (isProgress)
        {
            _uiThreadDispatcher?.Post(() =>
            {
                if (version != _loadVersion)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: 版本过期，跳过进度更新 (version={version}, current={_loadVersion})");
                    return;
                }
                var fixedMsg = FixMessage(message);
                ProgressChatMessage.TryParse(fixedMsg.Content, out var pcm);
                // 复制 base 字段（Id/会话/发送者/时间）；MessageType 必须设 "Progress"（否则选择器误路由 + 去重失效）。
                pcm.Id = fixedMsg.Id;
                pcm.ConversationId = fixedMsg.ConversationId;
                pcm.SenderId = fixedMsg.SenderId;
                pcm.SenderName = fixedMsg.SenderName;
                pcm.MessageType = "Progress";
                pcm.CreationTime = fixedMsg.CreationTime;
                // 审批请求：注入批准/拒绝命令（老板手机端直接决策 → REST 发回 → 桌面恢复 run）。
                if (pcm.IsApproval)
                {
                    pcm.ApproveCommand = new AsyncRelayCommand(() => SendApprovalDecisionAsync(true, pcm));
                    pcm.RejectCommand = new AsyncRelayCommand(() => SendApprovalDecisionAsync(false, pcm));
                }
                var idx = -1;
                for (int i = Messages.Count - 1; i >= 0; i--)
                    if (string.Equals(Messages[i].MessageType, "Progress", StringComparison.OrdinalIgnoreCase)
                        && Messages[i].SenderId == message.SenderId) { idx = i; break; }
                if (idx >= 0) Messages[idx] = pcm;
                else Messages.Add(pcm);
            });
            // Progress 不进 _displayedMessageIds（避免同 Id 心跳被拒）、不缓存、不 MarkAsRead。
            return;
        }

        // Text（含摘要）：收束——先移除同 sender 的进度槽位，再走原 Add 路径（结果替换进度心跳）。
        if (_displayedMessageIds.Add(message.Id))
        {
            _lastMessageTime = message.CreationTime;
            _uiThreadDispatcher?.Post(() =>
            {
                if (version == _loadVersion)
                {
                    for (int i = Messages.Count - 1; i >= 0; i--)
                        if (string.Equals(Messages[i].MessageType, "Progress", StringComparison.OrdinalIgnoreCase)
                            && Messages[i].SenderId == message.SenderId) { Messages.RemoveAt(i); break; }
                    Messages.Add(FixMessage(message));
                }
                else
                    System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: 版本过期，跳过 UI 更新 (version={version}, current={_loadVersion})");
            });

            if (_messageCacheService != null)
            {
                await Task.Run(() => _messageCacheService.AppendMessageAsync(conversationId, message));
            }

            await MarkAsReadAndNotifyAsync(conversationId);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] HandleMessage: 消息已显示，跳过重复 (Id={message.Id})");
        }
    }

    [RelayCommand]
    private async Task RefreshMessagesAsync()
    {
        if (_chatService == null || _currentConversationId == Guid.Empty) return;

        var version = ++_loadVersion;
        var conversationId = _currentConversationId;
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            Messages.Clear();
            _displayedMessageIds.Clear();
            _lastMessageTime = DateTime.MinValue;

            var response = await Task.Run(() => _chatService.GetMessagesAsync(conversationId, 0, MaxMessagesPerLoad));
            if (version != _loadVersion) return;
            if (response != null)
            {
                foreach (var msg in response)
                {
                    // 历史不残留进度心跳（Progress 是实时过程态，回看无意义）。
                    if (string.Equals(msg.MessageType, "Progress", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_displayedMessageIds.Add(msg.Id))
                        Messages.Add(FixMessage(msg));
                }
                _lastMessageTime = Messages.LastOrDefault()?.CreationTime ?? DateTime.MinValue;

                if (_messageCacheService != null && Messages.Any())
                {
                    await Task.Run(() => _messageCacheService.CacheMessagesAsync(conversationId, Messages.ToList(), MaxMessagesPerLoad));
                }
            }

            await MarkAsReadAndNotifyAsync(conversationId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"刷新失败: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CopyMessageAsync(string? content)
    {
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            await _clipboardService!.SetTextAsync(content);
            await _toastService!.ShowAsync("已复制");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"复制失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 切换语音识别状态（点击麦克风按钮或录音面板圆球时调用）
    /// 如果正在录音 → 停止；如果没在录音 → 开始
    /// </summary>
    [RelayCommand]
    private async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            // 停止录音
            _voiceCts?.Cancel();
            return;
        }

        await StartListeningCoreAsync();
    }

    /// <summary>
    /// 切换输入模式：语音模式 ↔ 文字模式
    /// </summary>
    [RelayCommand]
    private void ToggleInputMode()
    {
        IsVoiceMode = !IsVoiceMode;
    }

    /// <summary>
    /// 启动语音识别（按住说话触发，仅开始不停止）
    /// </summary>
    [RelayCommand]
    private async Task StartVoiceAsync()
    {
        if (IsListening) return;
        await StartListeningCoreAsync();
    }

    /// <summary>
    /// 语音识别核心逻辑
    /// </summary>
    private async Task StartListeningCoreAsync()
    {
        if (IsListening)
        {
            // 停止录音
            _voiceCts?.Cancel();
            return;
        }

        // 开始录音
        if (_speechRecognitionService == null) return;

        var granted = await _speechRecognitionService.RequestPermissionAsync();
        if (!granted)
        {
            _toastService?.ShowAsync("需要麦克风权限才能使用语音输入", 2000);
            return;
        }

        IsListening = true;
        _voiceCts = new CancellationTokenSource();
        var ct = _voiceCts.Token;

        try
        {
            var result = await _speechRecognitionService.ListenAsync(partial =>
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    // 实时更新输入框显示中间识别结果
                    MessageInput = partial;
                });
            }, ct);

            // 识别结束后，将最终文本填入输入框
            if (!string.IsNullOrWhiteSpace(result))
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    MessageInput = result;
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 用户点击停止，正常行为
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 语音识别失败: {ex.Message}");
        }
        finally
        {
            IsListening = false;
            _voiceCts?.Dispose();
            _voiceCts = null;
        }
    }

    /// <summary>
    /// 停止语音识别并发送（或取消）。由平台层在松开手指时调用。
    /// slideCanceled: 是否因上滑而取消
    /// </summary>
    public async Task StopVoiceAndSendAsync(bool slideCanceled)
    {
        if (!IsListening) return;

        // 停止录音
        _voiceCts?.Cancel();

        // 等待识别结束
        await Task.Delay(300);

        if (slideCanceled)
        {
            // 上滑取消：清空识别文字
            MessageInput = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(MessageInput))
        {
            // 非取消：自动发送
            if (SendMessageCommand.CanExecute(null))
                await SendMessageCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (_chatService == null || string.IsNullOrWhiteSpace(MessageInput)) return;
        if (_currentConversationId == Guid.Empty) return;

        var content = MessageInput;
        MessageInput = string.Empty;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 发送消息: convId={_currentConversationId}, content={content}");
            var message = await Task.Run(() => _chatService.SendMessageAsync(_currentConversationId, content));
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 发送响应: msgId={message?.Id}");

            if (message != null)
            {
                if (_displayedMessageIds.Add(message.Id))
                {
                    _lastMessageTime = message.CreationTime;
                    Messages.Add(FixMessage(message));
                }

                if (_messageCacheService != null)
                {
                    await Task.Run(() => _messageCacheService.AppendMessageAsync(_currentConversationId, message));
                }

                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = "发送失败: 未知错误";
                System.Diagnostics.Debug.WriteLine("[ChatDetail] 发送失败: 服务返回 null");
                MessageInput = content;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 发送失败: {ex.Message}");
            ErrorMessage = $"发送失败: {ex.Message}";
            MessageInput = content;
        }
    }

    /// <summary>老板手机端审批决策：序列化 RemoteApprovalDecision → REST 以 "ApprovalDecision" type 发回桌面。
    /// 桌面 HandleRemoteApprovalDecision 解 _remoteApprovals 的 pending TCS → run 协作循环带决策重入恢复。
    /// 发完后把本卡替换为已决策态（POCO replace：✅已批准/❌已拒绝，按钮隐）。桌面解 TCS 后会发后续 Progress（RunDone）+ 摘要 Text 收束。</summary>
    private async Task SendApprovalDecisionAsync(bool approved, ProgressChatMessage pcm)
    {
        if (_chatService == null || _currentConversationId == Guid.Empty) return;
        var dto = new RemoteApprovalDecision(approved, approved ? "老板批准" : "老板拒绝");
        var json = JsonSerializer.Serialize(dto);
        System.Diagnostics.Debug.WriteLine($"[ChatDetail] 发送审批决策: convId={_currentConversationId}, approved={approved}");
        try
        {
            await Task.Run(() => _chatService.SendMessageAsync(_currentConversationId, json, "ApprovalDecision"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 发送审批决策失败: {ex.Message}");
        }
        // 替换本卡为已决策态（POCO replace：spinner 停、按钮隐、显 ✅已批准/❌已拒绝）。
        _uiThreadDispatcher?.Post(() =>
        {
            var idx = Messages.IndexOf(pcm);
            if (idx >= 0)
            {
                var resolved = new ProgressChatMessage
                {
                    Id = pcm.Id, ConversationId = pcm.ConversationId, SenderId = pcm.SenderId,
                    SenderName = pcm.SenderName, MessageType = "Progress", CreationTime = pcm.CreationTime,
                    Content = pcm.Content, Progress = pcm.Progress,
                    Icon = approved ? "✅" : "❌",
                    DisplayLabel = approved ? "已批准" : "已拒绝",
                    IsInProgress = false, IsApproval = false,
                };
                Messages[idx] = resolved;
            }
        });
    }

    private async Task SyncMissedMessagesAsync()
    {
        if (_chatService == null || _currentConversationId == Guid.Empty) return;

        var version = _loadVersion;
        try
        {
            var response = await Task.Run(() => _chatService.GetMessagesAsync(_currentConversationId));
            if (version != _loadVersion) return;
            if (response == null) return;

            var missed = response.Where(m => m.CreationTime > _lastMessageTime
                && !string.Equals(m.MessageType, "Progress", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!missed.Any()) return;

            foreach (var msg in missed)
            {
                if (_displayedMessageIds.Add(msg.Id))
                {
                    var fixedMsg = FixMessage(msg);
                    _uiThreadDispatcher?.Post(() =>
                    {
                        Messages.Add(fixedMsg);
                    });
                }
            }
            _lastMessageTime = Messages.LastOrDefault()?.CreationTime ?? DateTime.MinValue;

            // 补漏的消息也保存到本地缓存
            if (_messageCacheService != null && Messages.Any())
            {
                await Task.Run(() => _messageCacheService.CacheMessagesAsync(_currentConversationId, Messages.ToList(), MaxMessagesPerLoad));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatDetail] 补漏失败: {ex.Message}");
        }
    }
}
