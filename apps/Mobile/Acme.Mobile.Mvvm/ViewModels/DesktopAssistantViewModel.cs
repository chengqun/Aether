using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using Acme.DeviceManagement.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 桌面助手 ViewModel：手机远程控制桌面 agent 的**独立**功能，不复用社交聊天的 <see cref="ChatDetailViewModel"/>。
/// 设备对话走自己的精简链路——设备列表、设备会话创建、消息收发、SignalR 重连补拉。
/// 历史包袱已去除：无本地 SQLite 消息缓存/会话同步（社交聊天秒开优化，设备对话每次拉最新即可）、
/// 无语音输入、无群组/好友/服务号分支、不依赖 <c>ChatDetailViewModel.CachedUserId</c> 静态。
/// </summary>
public partial class DesktopAssistantViewModel : NavigableViewModelBase
{
    private readonly IChatService? _chatService;
    private readonly ISignalRChatService? _signalRChatService;
    private readonly IEventBusService? _eventBusService;
    private readonly ICurrentUser? _currentUser;
    private readonly IDeviceService? _deviceService;
    private readonly IGlobalMessageHandler? _globalMessageHandler;
    private readonly IAppSettings? _appSettings;

    private Guid _currentConversationId = Guid.Empty;
    private Guid _currentUserId = Guid.Empty;
    private int _loadVersion;               // 会话切换/重载时递增，废弃过期异步结果
    private bool _devicesLoaded = false;
    private bool _syncingDevicePicker = false;  // 程序化回填设备下拉索引时抑制切换副作用
    private readonly HashSet<Guid> _displayedMessageIds = new();  // 按消息 Id 去重

    /// <summary>消息列表（UI 绑定）。</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>桌面设备下拉选项。</summary>
    public ObservableCollection<DesktopDeviceOption> Devices { get; } = new();

    [ObservableProperty]
    private int _selectedDeviceIndex = -1;

    [ObservableProperty]
    private string _messageInput = string.Empty;

    [ObservableProperty]
    private bool _hasMessageInput;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _conversationName = "桌面助手";

    // ⚠️ 临时诊断：实时收不到桌面回复时，页面顶部显示 SignalR 连接态 + 收到的消息事件，
    // 供无 iOS 日志可抓时人工定位（确认后移除）。
    [ObservableProperty]
    private string? _signalRDebug;

    /// <summary>当前用户 Id（供消息模板选择器区分「我的」/「桌面」气泡）。</summary>
    public Guid CachedUserId => _currentUserId;

    public DesktopAssistantViewModel(
        IChatService? chatService = null,
        ISignalRChatService? signalRChatService = null,
        IEventBusService? eventBusService = null,
        ICurrentUser? currentUser = null,
        IDeviceService? deviceService = null,
        IUIThreadDispatcher? uiThreadDispatcher = null,
        IGlobalMessageHandler? globalMessageHandler = null,
        IAppSettings? appSettings = null)
    {
        _chatService = chatService;
        _signalRChatService = signalRChatService;
        _eventBusService = eventBusService;
        _currentUser = currentUser;
        _deviceService = deviceService;
        _uiThreadDispatcher = uiThreadDispatcher;
        _globalMessageHandler = globalMessageHandler;
        _appSettings = appSettings;
    }

    partial void OnMessageInputChanged(string value)
    {
        HasMessageInput = !string.IsNullOrWhiteSpace(value);
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    public override async Task OnActivatedAsync()
    {
        ResetState();
        var version = _loadVersion;

        try
        {
            // 解析当前用户 Id（供气泡模板区分 + 发消息）。
            _currentUserId = await ResolveCurrentUserIdAsync();
            if (version != _loadVersion) return;

            // 首次加载 desktop 设备列表（下拉选项）。
            if (!_devicesLoaded)
            {
                _devicesLoaded = true;
                await LoadDesktopDevicesAsync();
                if (version != _loadVersion) return;
            }

            // 默认选中第一台设备并建/恢复会话（无设备则提示）。
            if (Devices.Count > 0)
            {
                var idx = SelectedDeviceIndex >= 0 ? SelectedDeviceIndex : 0;
                var device = Devices[idx];
                // 服务端 get-or-create-device-conversation 把 Name 设为 device.DeviceName，故本地等价回填即可，
                // 命中缓存跳过 POST 时也有会话名。
                ConversationName = string.IsNullOrWhiteSpace(device.DeviceName) ? "桌面助手" : device.DeviceName;
                _currentConversationId = await EnsureDeviceConversationAsync(device.DeviceId);
                if (version != _loadVersion) return;

                SubscribeToEvents();
                // 桌面助手页冷启动也须自行拉起 SignalR 传输 + 消息事件发布链。这两步原本只在社交聊天
                // ChatViewModel.OnActivatedAsync 调用——用户直接进桌面助手页（未先开聊天 tab）时从未接线，
                // JoinConversationAsync 因「未连接」早返回 → 手机不在 conv_{id} 组 → 桌面回复实时推送收不到
                // （REST 拉取仍正常，故表现为「能发、收不到实时」）。
                EnsureSignalRListening();
                await EnsureSignalRConnectedAsync();
                if (version != _loadVersion) return;
                SignalRDebug = $"🔍 SignalR={(_signalRChatService?.IsConnected == true ? "已连接" : "未连接")} 服务器={_appSettings?.ServerBaseAddress ?? "(空)"} 会话={_currentConversationId}";
                await JoinConversationGroupAsync(_currentConversationId);
                await LoadMessagesAsync(version);
            }
            else
            {
                ErrorMessage = "暂无绑定桌面电脑，请先到「我 → 设备管理」绑定";
            }
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = $"加载失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 初始化失败: {ex}");
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    public override Task OnDeactivatedAsync()
    {
        UnsubscribeFromEvents();
        return Task.CompletedTask;
    }

    private void ResetState()
    {
        _loadVersion++;
        Messages.Clear();
        _displayedMessageIds.Clear();
        ErrorMessage = null;
        IsLoading = true;
        // 不重置 Devices / _devicesLoaded——列表复用，切换仅在索引层。
    }

    private async Task<Guid> ResolveCurrentUserIdAsync()
    {
        if (_currentUserId != Guid.Empty) return _currentUserId;
        if (_currentUser == null) return Guid.Empty;
        var raw = await _currentUser.GetCurrentUserIdAsync();
        if (Guid.TryParse(raw, out var uid))
        {
            _currentUserId = uid;
            return uid;
        }
        return Guid.Empty;
    }

    /// <summary>加载 desktop 设备列表填充「选择电脑」下拉；默认选中第一台。</summary>
    private async Task LoadDesktopDevicesAsync()
    {
        if (_deviceService == null) return;
        try
        {
            var devices = await Task.Run(async () => await _deviceService.GetMyDevicesAsync());
            var desktop = devices?.Where(d =>
            {
                var t = d.DeviceType ?? string.Empty;
                return t.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("windows", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("macos", StringComparison.OrdinalIgnoreCase);
            }).ToList() ?? new List<MyDevice>();

            Devices.Clear();
            foreach (var d in desktop)
            {
                if (!string.IsNullOrWhiteSpace(d.DeviceId))
                    Devices.Add(new DesktopDeviceOption { DeviceId = d.DeviceId, DeviceName = d.DeviceName, DeviceType = d.DeviceType });
            }

            if (Devices.Count > 0)
            {
                _syncingDevicePicker = true;
                SelectedDeviceIndex = 0;
                _syncingDevicePicker = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 加载桌面设备失败: {ex.Message}");
        }
    }

    /// <summary>设备↔用户会话 1:1 且稳定（get-or-create 每次返回同一会话 Id）。跨 VM 实例缓存命中即跳过 POST，
    /// 省掉服务端 GetLastMessageAsync 等全量查询——重复切换设备/重新进入页面时秒开。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> s_deviceConversationCache = new();

    /// <summary>按 32 位 DeviceId 建/恢复用户↔设备会话，返回会话 Id。</summary>
    private async Task<Guid> EnsureDeviceConversationAsync(string deviceId)
    {
        if (_chatService == null) return Guid.Empty;
        if (s_deviceConversationCache.TryGetValue(deviceId, out var cached) && cached != Guid.Empty)
            return cached;
        try
        {
            var conv = await Task.Run(() => _chatService.GetOrCreateDeviceConversationAsync(deviceId));
            if (conv == null)
            {
                ErrorMessage = "创建桌面会话失败";
                return Guid.Empty;
            }
            s_deviceConversationCache[deviceId] = conv.Id;
            return conv.Id;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"创建桌面会话失败: {ex.Message}";
            return Guid.Empty;
        }
    }

    /// <summary>切换设备：重设会话并重载（_loadVersion 已使旧异步结果作废）。</summary>
    partial void OnSelectedDeviceIndexChanged(int value)
    {
        if (_syncingDevicePicker) return;
        if (value < 0 || value >= Devices.Count) return;
        _ = OnActivatedAsync();
    }

    private async Task LoadMessagesAsync(int version)
    {
        if (_chatService == null || _currentConversationId == Guid.Empty) return;
        try
        {
            var messages = await Task.Run(() => _chatService.GetMessagesAsync(_currentConversationId, 0, MaxMessagesPerLoad));
            if (version != _loadVersion) return;

            Messages.Clear();
            _displayedMessageIds.Clear();
            if (messages != null)
            {
                foreach (var msg in messages.OrderBy(m => m.CreationTime))
                {
                    // 历史不残留进度心跳 / 审批决策（过程态控制消息）。
                    if (string.Equals(msg.MessageType, "Progress", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(msg.MessageType, "ApprovalDecision", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_displayedMessageIds.Add(msg.Id))
                        Messages.Add(FixMessage(msg));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 加载消息失败: {ex.Message}");
        }
    }

    private const int MaxMessagesPerLoad = 50;

    private ChatMessage FixMessage(ChatMessage msg)
    {
        msg.CreationTime = msg.CreationTime.Kind == DateTimeKind.Local
            ? msg.CreationTime
            : DateTime.SpecifyKind(msg.CreationTime, DateTimeKind.Utc).ToLocalTime();
        return msg;
    }

    private void SubscribeToEvents()
    {
        UnsubscribeFromEvents();
        _eventBusService?.Subscribe<NewMessageEvent>(OnNewMessage);
        _eventBusService?.Subscribe<SignalRReconnectedEvent>(OnSignalRReconnected);
    }

    private void UnsubscribeFromEvents()
    {
        _eventBusService?.Unsubscribe<NewMessageEvent>(OnNewMessage);
        _eventBusService?.Unsubscribe<SignalRReconnectedEvent>(OnSignalRReconnected);
    }

    private async void OnNewMessage(NewMessageEvent @event)
    {
        var msg = @event.Message;
        // ⚠️ 临时诊断：任何消息到达都记录，便于判断是「SignalR 没连上」还是「事件没发布」还是「被过滤」。
        var matched = msg.ConversationId == _currentConversationId;
        var mine = msg.SenderId == _currentUserId;
        _uiThreadDispatcher?.Post(() =>
            SignalRDebug = $"📨 收到消息 conv匹配={matched} 自发={mine} 类型={msg.MessageType} 发送者={msg.SenderId}");
        if (msg.ConversationId != _currentConversationId) return;
        if (msg.SenderId == _currentUserId) return;  // 自发自收
        // 进度心跳不渲染为气泡（桌面助手只显示概览摘要/最终答案）。
        if (string.Equals(msg.MessageType, "Progress", StringComparison.OrdinalIgnoreCase)) return;

        if (_displayedMessageIds.Add(msg.Id))
        {
            _uiThreadDispatcher?.Post(() =>
            {
                Messages.Add(FixMessage(msg));
            });
            await MarkAsReadAsync();
        }
    }

    private async void OnSignalRReconnected(SignalRReconnectedEvent @event)
    {
        // 重连产生新 connectionId，旧的 conv_{id} 组成员资格失效，须重新入组再补拉错过的消息。
        var version = _loadVersion;
        await JoinConversationGroupAsync(_currentConversationId);
        await LoadMessagesAsync(version);
    }

    /// <summary>接社交聊天未先进入时，桌面助手页冷启动也要自行拉起 SignalR 消息事件发布链。
    /// GlobalMessageHandler 是单例，StartListening/StartListeningReconnected 用 -=/+= 幂等订阅
    /// OnMessageReceived（发布 NewMessageEvent）与 OnReconnected（发布 SignalRReconnectedEvent）。
    /// 二者原本只在 ChatViewModel.OnActivatedAsync 调用，直接进桌面助手页时从未接线 →
    /// 即便 SignalR 已连接，OnMessageReceived 也无人发布 NewMessageEvent → OnNewMessage 永不触发。</summary>
    private void EnsureSignalRListening()
    {
        _globalMessageHandler?.StartListening();
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
            SignalRDebug = $"⚠️ SignalR 连接异常: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] SignalR 连接失败: {ex.Message}");
        }
    }

    private async Task JoinConversationGroupAsync(Guid conversationId)
    {
        if (_signalRChatService == null || conversationId == Guid.Empty) return;
        try
        {
            await Task.Run(() => _signalRChatService.JoinConversationAsync(conversationId));
        }
        catch (Exception ex)
        {
            SignalRDebug = $"⚠️ 加入会话组失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 加入会话组失败: {ex.Message}");
        }
    }

    private async Task MarkAsReadAsync()
    {
        if (_chatService == null || _currentConversationId == Guid.Empty) return;
        try
        {
            await Task.Run(() => _chatService.MarkAsReadAsync(_currentConversationId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 标记已读失败: {ex.Message}");
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
            var message = await Task.Run(() => _chatService.SendMessageAsync(_currentConversationId, content));
            if (message != null && _displayedMessageIds.Add(message.Id))
            {
                Messages.Add(FixMessage(message));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"发送失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[DesktopAssistant] 发送失败: {ex}");
        }
    }
}
