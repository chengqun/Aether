using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Aether.Server.HttpApi.Services;

/// <summary>
/// 进程内设备 SignalR 连接追踪器。
/// <para>设备经 <c>authorizationCode</c> 连接 <see cref="Hubs.ChatHub"/>（非 JWT、无 <c>sub</c> claim），
/// 而 <c>SubClaimUserIdProvider</c> 只认 <c>sub</c> claim → <c>Clients.User(deviceId)</c> 触达不了设备连接。
/// 故 <c>NotifyNewConversationAsync</c>（用 <c>Clients.User</c>）对设备无效——设备无法被动收到「新会话已建」通知去 join 组。</para>
/// <para>此追踪器维护 <c>deviceId→活跃 connectionId</c> 映射，让服务端在设备会话创建/发送时主动把已连接的设备
/// 拉进 <c>conv_{id}</c> 组，关闭「桌面先连接、手机后建会话 → 桌面不在组里收不到消息」的竞态。
/// 桌面端连接晚于会话创建时，由 <c>ChatHub.OnConnectedAsync</c> 既有自动 join 兜底（设备是 participant）。</para>
/// </summary>
public interface IDeviceConnectionTracker
{
    /// <summary>设备连接建立时登记。deviceId 为设备主键（非 32 位 DeviceId 字符串）。</summary>
    void Register(Guid deviceId, string connectionId);

    /// <summary>连接断开时注销（按 connectionId 反查 deviceId）。</summary>
    void Unregister(string connectionId);

    /// <summary>取某设备当前所有活跃连接（可能多端同时在线）。</summary>
    IReadOnlyCollection<string> GetConnections(Guid deviceId);
}

public class DeviceConnectionTracker : IDeviceConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _deviceToConnections = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToDevice = new();

    public void Register(Guid deviceId, string connectionId)
    {
        if (deviceId == Guid.Empty || string.IsNullOrEmpty(connectionId)) return;
        var set = _deviceToConnections.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, byte>());
        set.TryAdd(connectionId, 0);
        _connectionToDevice[connectionId] = deviceId;
    }

    public void Unregister(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return;
        if (_connectionToDevice.TryRemove(connectionId, out var deviceId) &&
            _deviceToConnections.TryGetValue(deviceId, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty)
                _deviceToConnections.TryRemove(deviceId, out _);
        }
    }

    public IReadOnlyCollection<string> GetConnections(Guid deviceId)
    {
        if (_deviceToConnections.TryGetValue(deviceId, out var set))
            return set.Keys.ToArray();
        return Array.Empty<string>();
    }
}
