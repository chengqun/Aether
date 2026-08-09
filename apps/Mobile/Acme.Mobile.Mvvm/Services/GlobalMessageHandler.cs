using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Services;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Events;

namespace Acme.Mobile.Mvvm.Services;

public interface IGlobalMessageHandler
{
    void StartListening();
    void StopListening();
    void StartListeningReconnected();
}

public class GlobalMessageHandler : IGlobalMessageHandler
{
    private readonly ISignalRChatService? _signalRChatService;
    private readonly IEventBusService? _eventBusService;
    private readonly IUnreadMessageCacheService? _unreadMessageCacheService;

    public GlobalMessageHandler(ISignalRChatService? signalRChatService,
                               IEventBusService? eventBusService,
                               IUnreadMessageCacheService? unreadMessageCacheService)
    {
        _signalRChatService = signalRChatService;
        _eventBusService = eventBusService;
        _unreadMessageCacheService = unreadMessageCacheService;
    }

    public void StartListening()
    {
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] StartListening 被调用");

        if (_signalRChatService == null)
        {
            System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] _signalRChatService 为空，无法启动监听");
            return;
        }

        _signalRChatService.OnMessageReceived -= OnSignalRMessageReceived;
        _signalRChatService.OnMessageReceived += OnSignalRMessageReceived;
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] OnMessageReceived 事件订阅成功");

        _signalRChatService.OnUnreadCountUpdated -= OnSignalRUnreadCountUpdated;
        _signalRChatService.OnUnreadCountUpdated += OnSignalRUnreadCountUpdated;
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] OnUnreadCountUpdated 事件订阅成功");

        _signalRChatService.OnNewConversation -= OnSignalRNewConversation;
        _signalRChatService.OnNewConversation += OnSignalRNewConversation;
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] OnNewConversation 事件订阅成功");

        System.Diagnostics.Debug.WriteLine($"[GlobalMessageHandler] SignalR 连接状态: {_signalRChatService.IsConnected}");
    }

    public void StopListening()
    {
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] StopListening 被调用");

        if (_signalRChatService == null) return;

        _signalRChatService.OnMessageReceived -= OnSignalRMessageReceived;
        _signalRChatService.OnUnreadCountUpdated -= OnSignalRUnreadCountUpdated;
        _signalRChatService.OnNewConversation -= OnSignalRNewConversation;
    }

    private async Task OnSignalRMessageReceived(ChatMessage message)
    {
        System.Diagnostics.Debug.WriteLine($"[GlobalMessageHandler] 收到消息: ConversationId={message.ConversationId}, SenderId={message.SenderId}, Content={message.Content?.Substring(0, Math.Min(message.Content?.Length ?? 0, 30))}...");

        _eventBusService?.Publish(new NewMessageEvent(message));
        _unreadMessageCacheService?.CacheMessage(message);
    }

    private async Task OnSignalRUnreadCountUpdated(Guid conversationId, Guid userId, int unreadCount)
    {
        System.Diagnostics.Debug.WriteLine($"[GlobalMessageHandler] 收到未读计数更新: ConversationId={conversationId}, UserId={userId}, UnreadCount={unreadCount}");
        _eventBusService?.Publish(new UnreadCountUpdatedEvent(conversationId, userId, unreadCount));
    }

    public void StartListeningReconnected()
    {
        if (_signalRChatService == null) return;

        _signalRChatService.OnReconnected -= OnSignalRReconnected;
        _signalRChatService.OnReconnected += OnSignalRReconnected;
    }

    private async Task OnSignalRReconnected()
    {
        System.Diagnostics.Debug.WriteLine("[GlobalMessageHandler] SignalR 重连，发布 SignalRReconnectedEvent");
        _eventBusService?.Publish(new SignalRReconnectedEvent());
    }

    private void OnSignalRNewConversation(Guid conversationId)
    {
        System.Diagnostics.Debug.WriteLine($"[GlobalMessageHandler] 收到新会话通知: ConversationId={conversationId}");
        _eventBusService?.Publish(new NewConversationFromServerEvent(conversationId));
    }
}