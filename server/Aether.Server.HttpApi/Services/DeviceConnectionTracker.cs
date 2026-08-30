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

    /// <summary>设备上报当前选中的专家/目标显示名（手机只读状态用）。null/空 = 未选用。
    /// 进程内内存态：服务重启后丢失，桌面端（重）连接时重新上报覆盖。</summary>
    void SetSelectedAgent(Guid deviceId, string? agentName);

    /// <summary>取设备最近上报的选中专家名（可能设备已离线，保留「上次已知」供手机显示）。</summary>
    string? GetSelectedAgent(Guid deviceId);
}

public class DeviceConnectionTracker : IDeviceConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _deviceToConnections = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToDevice = new();
    // 设备最近上报的选中专家名（保留「上次已知」，设备离线后仍供手机只读显示；不随连接断开清空）。
    private readonly ConcurrentDictionary<Guid, string?> _selectedAgents = new();

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

    public void SetSelectedAgent(Guid deviceId, string? agentName)
    {
        if (deviceId == Guid.Empty) return;
        // 空/null 统一存空串（便于手机端「未选用」判断），不删条目——保留槽位供离线后显示「上次已知」。
        _selectedAgents[deviceId] = string.IsNullOrWhiteSpace(agentName) ? string.Empty : agentName.Trim();
    }

    public string? GetSelectedAgent(Guid deviceId)
    {
        return _selectedAgents.TryGetValue(deviceId, out var name) ? name : null;
    }
}
