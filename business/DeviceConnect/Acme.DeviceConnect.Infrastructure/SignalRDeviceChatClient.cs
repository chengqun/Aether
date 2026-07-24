using Acme.DeviceConnect.Domain;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.DeviceConnect.Infrastructure;

public class SignalRDeviceChatClient : IDeviceChatClient
{
    private HubConnection? _connection;
    private readonly ConcurrentDictionary<Guid, byte> _recentSentMessageIds = new();

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event Func<DeviceMessage, Task>? OnMessageReceived;

    public event Action<string>? ConnectionStateChanged;

    public async Task ConnectAsync(string baseUrl, string authorizationCode, string deviceName, string deviceType, string tenant = "default")
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("授权码不能为空", nameof(authorizationCode));

        if (_connection != null)
        {
            await DisconnectAsync();
        }

        var hubUrl = $"{baseUrl.TrimEnd('/')}/hubs/chat?__tenant={Uri.EscapeDataString(tenant)}&authorizationCode={Uri.EscapeDataString(authorizationCode)}&deviceName={Uri.EscapeDataString(deviceName)}&deviceType={Uri.EscapeDataString(deviceType)}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.On<JsonElement>("ReceiveMessage", async message =>
        {
            var msgId = message.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty;
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var convId = message.TryGetProperty("conversationId", out var ci) ? ci.GetGuid() : Guid.Empty;
            var senderName = message.TryGetProperty("senderName", out var sn) ? sn.GetString() ?? "未知" : "未知";

            if (convId == Guid.Empty) return;

            if (msgId != Guid.Empty && _recentSentMessageIds.TryRemove(msgId, out _))
                return;

            var msg = new DeviceMessage
            {
                Id = msgId,
                Content = content,
                ConversationId = convId,
                SenderName = senderName
            };

            if (OnMessageReceived != null)
                await OnMessageReceived.Invoke(msg);
        });

        _connection.Reconnected += async (_) =>
        {
            ConnectionStateChanged?.Invoke("重新连接成功");
            await Task.CompletedTask;
        };

        _connection.Closed += (error) =>
        {
            ConnectionStateChanged?.Invoke($"连接断开: {error?.Message ?? "未知原因"}，将自动重连...");
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync();
            ConnectionStateChanged?.Invoke("已连接");
        }
        catch (Exception ex)
        {
            ConnectionStateChanged?.Invoke($"连接失败: {ex.Message}");
            throw;
        }
    }

    public async Task<Guid> SendMessageAsync(Guid conversationId, string content, string messageType = "Text")
    {
        if (_connection == null || !IsConnected)
            throw new InvalidOperationException("未连接，无法发送消息");

        var sentMsg = await _connection.InvokeAsync<JsonElement>("SendMessage", conversationId, content, messageType);
        var sentId = sentMsg.TryGetProperty("id", out var sid) ? sid.GetGuid() : Guid.Empty;
        if (sentId != Guid.Empty)
            _recentSentMessageIds.TryAdd(sentId, 0);
        return sentId;
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            try
            {
                await _connection.StopAsync();
                await _connection.DisposeAsync();
            }
            catch
            {
            }
            _connection = null;
            _recentSentMessageIds.Clear();
            ConnectionStateChanged?.Invoke("已断开");
        }
    }
}