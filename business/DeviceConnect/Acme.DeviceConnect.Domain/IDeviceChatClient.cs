using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.DeviceConnect.Domain;

public interface IDeviceChatClient
{
    bool IsConnected { get; }
    event Func<DeviceMessage, Task>? OnMessageReceived;
    event Action<string>? ConnectionStateChanged;
    Task ConnectAsync(string baseUrl, string authorizationCode, string deviceName, string deviceType, string tenant = "default");
    Task<Guid> SendMessageAsync(Guid conversationId, string content, string messageType = "Text");
    /// <summary>设备重连/冷启动补拉：取回离线期间未被设备回复的最后一条用户消息（每会话至多一条）。</summary>
    Task<IReadOnlyList<DeviceMessage>> GetCatchUpMessagesAsync();
    /// <summary>上报当前选中的专家/目标显示名给服务端（手机只读状态用）。空/null = 未选用。
    /// 桌面端在目标切换 +（重）连接成功后调用。</summary>
    Task ReportSelectedAgentAsync(string? agentName);
    Task DisconnectAsync();
}