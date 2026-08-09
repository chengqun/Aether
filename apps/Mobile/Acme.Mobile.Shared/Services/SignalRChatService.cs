using Acme.AppAuth.Domain;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 社交聊天 SignalR 实时推送服务（手机端独有）。移植自 AlphaAgent <c>SignalRChatService</c>，
/// 适配 Aether 契约：JWT 经 <see cref="IAppTokenStore"/> 持有的 access token；自签名 CA 经
/// <see cref="Hosting.MobileCertificateHandler.CreateHandler"/>；消息体用 <see cref="ChatMessage"/>。
/// <para>连接外部 ABP 后端 <c>{baseUrl}/hubs/chat</c> hub。仅收 + JoinConversation——发送走 REST
/// (<c>IChatService.SendMessageAsync</c>)，hub 不 SendAsync。Singleton；已连接则跳过重建。</para>
/// <para>参照：<c>SignalRDeviceChatClient</c>（DeviceConnect）已镜像同一 hub 的 ReceiveMessage 契约。</para>
/// </summary>
public sealed class SignalRChatService : ISignalRChatService
{
    private HubConnection? _hubConnection;
    private readonly IAppTokenStore _tokenStore;
    private readonly Func<HttpMessageHandler>? _httpMessageHandlerFactory;

    public SignalRChatService(IAppTokenStore tokenStore, Func<HttpMessageHandler>? httpMessageHandlerFactory = null)
    {
        _tokenStore = tokenStore;
        _httpMessageHandlerFactory = httpMessageHandlerFactory;
    }

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public event Func<ChatMessage, Task>? OnMessageReceived;
    public event Func<Guid, Guid, int, Task>? OnUnreadCountUpdated;
    public event Func<Task>? OnReconnected;
    public event Action<Guid>? OnNewConversation;

    public async Task ConnectAsync(string baseUrl)
    {
        // 已存在且非断开 → 跳过重建（Singleton 复用）
        if (_hubConnection != null && _hubConnection.State != HubConnectionState.Disconnected)
        {
            System.Diagnostics.Debug.WriteLine($"[SignalR] 连接已存在，状态: {_hubConnection.State}，跳过重建");
            return;
        }

        if (_hubConnection != null)
            await DisconnectAsync();

        var hubUrl = baseUrl.TrimEnd('/') + "/hubs/chat";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => _tokenStore.GetAccessTokenAsync();
                if (_httpMessageHandlerFactory != null)
                    options.HttpMessageHandlerFactory = _ => _httpMessageHandlerFactory();
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.FromSeconds(0),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
            })
            .Build();

        _hubConnection.On<ChatMessage>("ReceiveMessage", async message =>
        {
            if (OnMessageReceived != null)
                await OnMessageReceived.Invoke(message);
        });

        _hubConnection.On<Guid, Guid, int>("UnreadCountUpdated", async (conversationId, userId, unreadCount) =>
        {
            if (OnUnreadCountUpdated != null)
                await OnUnreadCountUpdated.Invoke(conversationId, userId, unreadCount);
        });

        _hubConnection.On<Guid>("NewConversation", conversationId =>
            OnNewConversation?.Invoke(conversationId));

        _hubConnection.Reconnected += async _ =>
        {
            if (OnReconnected != null)
                await OnReconnected.Invoke();
        };

        System.Diagnostics.Debug.WriteLine($"[SignalR] 正在连接到 {hubUrl}");
        await _hubConnection.StartAsync();
        System.Diagnostics.Debug.WriteLine($"[SignalR] 连接成功，状态: {_hubConnection.State}");
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync();
                await _hubConnection.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] Disconnect 异常: {ex.Message}");
            }
            _hubConnection = null;
        }
    }

    public async Task JoinConversationAsync(Guid conversationId)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            System.Diagnostics.Debug.WriteLine("[SignalR] JoinConversation 跳过：未连接");
            return;
        }

        try
        {
            await _hubConnection.InvokeAsync("JoinConversation", conversationId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SignalR] JoinConversation 失败: {ex.Message}");
        }
    }
}
