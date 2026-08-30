using Acme.DeviceConnect.Domain;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.DeviceConnect.Infrastructure;

public class SignalRDeviceChatClient : IDeviceChatClient
{
    private HubConnection? _connection;
    // 自己发出消息的 Id 去重集——服务端对设备发送方已用 GroupExcept 不回广播（见 SignalRChatNotifier），
    // 此 LRU 为防御性兜底：若因多连接/追踪器边缘竞态仍有 echo 漏过则按 Id 丢弃。
    // 有界 LRU：消费即移除（回广播只来一次）；超量淘汰最旧——防止无限重连长连接下无限增长。
    private readonly object _recentSentLock = new();
    private readonly HashSet<Guid> _recentSentMessageIds = new();
    private readonly Queue<Guid> _recentSentQueue = new();
    private const int MaxRecentSentIds = 500;

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
            // 无限指数退避重连：首次 0s 立即，之后 2^n 秒封顶 60s + 0~2s 抖动。
            // 旧版固定 4 次重连（0/2/5/10s）后放弃 → 网络长抖动后桌面永不自愈，需手动重启 app 才恢复。
            .WithAutomaticReconnect(new ExponentialBackoffRetryPolicy())
            .Build();

        _connection.On<JsonElement>("ReceiveMessage", async message =>
        {
            var msgId = message.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty;
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var convId = message.TryGetProperty("conversationId", out var ci) ? ci.GetGuid() : Guid.Empty;
            var senderName = message.TryGetProperty("senderName", out var sn) ? sn.GetString() ?? "未知" : "未知";
            // messageType 透传（服务端 ChatMessageDto 已含；旧消息无此字段 → 默认 "Text"）。
            var messageType = message.TryGetProperty("messageType", out var mt) ? mt.GetString() ?? "Text" : "Text";

            if (convId == Guid.Empty) return;

            if (msgId != Guid.Empty && TryConsumeRecentSentId(msgId))
                return;

            var msg = new DeviceMessage
            {
                Id = msgId,
                Content = content,
                ConversationId = convId,
                SenderName = senderName,
                MessageType = messageType
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
            AddRecentSentId(sentId);
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
            lock (_recentSentLock)
            {
                _recentSentMessageIds.Clear();
                _recentSentQueue.Clear();
            }
            ConnectionStateChanged?.Invoke("已断开");
        }
    }

    public async Task ReportSelectedAgentAsync(string? agentName)
    {
        if (_connection == null || !IsConnected) return;
        try
        {
            // 上报失败不影响连接与正常收发——静默吞掉（手机状态是次要显示）。
            await _connection.InvokeAsync("ReportSelectedAgent", agentName ?? string.Empty);
        }
        catch
        {
        }
    }

    public async Task<IReadOnlyList<DeviceMessage>> GetCatchUpMessagesAsync()
    {
        if (_connection == null || !IsConnected)
            return Array.Empty<DeviceMessage>();
        try
        {
            var arr = await _connection.InvokeAsync<JsonElement[]>("GetCatchUpMessages");
            if (arr == null || arr.Length == 0)
                return Array.Empty<DeviceMessage>();
            var list = new List<DeviceMessage>(arr.Length);
            foreach (var m in arr)
            {
                list.Add(new DeviceMessage
                {
                    Id = m.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty,
                    Content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    ConversationId = m.TryGetProperty("conversationId", out var ci) ? ci.GetGuid() : Guid.Empty,
                    SenderName = m.TryGetProperty("senderName", out var sn) ? sn.GetString() ?? "用户" : "用户",
                    MessageType = m.TryGetProperty("messageType", out var mt) ? mt.GetString() ?? "Text" : "Text"
                });
            }
            return list;
        }
        catch
        {
            // 补拉失败不应影响已建立的连接与正常收发——静默退化为「无补拉」。
            return Array.Empty<DeviceMessage>();
        }
    }

    private void AddRecentSentId(Guid id)
    {
        if (id == Guid.Empty) return;
        lock (_recentSentLock)
        {
            if (!_recentSentMessageIds.Add(id)) return;
            _recentSentQueue.Enqueue(id);
            while (_recentSentQueue.Count > MaxRecentSentIds)
                _recentSentMessageIds.Remove(_recentSentQueue.Dequeue());
        }
    }

    private bool TryConsumeRecentSentId(Guid id)
    {
        if (id == Guid.Empty) return false;
        lock (_recentSentLock)
            return _recentSentMessageIds.Remove(id);  // 消费即移除（回广播只来一次）
    }
}

/// <summary>无限指数退避重连策略：首次 0s 立即，之后 2^n 秒封顶 60s + 0~2s 抖动。</summary>
internal sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext context)
    {
        // PreviousRetryCount 为 0 = 首次重试，立即重连（覆盖瞬断）。
        var n = context.PreviousRetryCount;
        if (n == 0) return TimeSpan.Zero;
        var secs = Math.Min(Math.Pow(2, n), 60);  // 2,4,8,16,32,60,60…
        var jitter = Random.Shared.NextDouble() * 2;  // 0~2s 抖动，避免多端同步重连风暴
        return TimeSpan.FromSeconds(secs + jitter);
    }
}