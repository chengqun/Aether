using System;
using System.Threading.Tasks;

namespace Acme.DeviceConnect.Domain;

public interface IDeviceChatClient
{
    bool IsConnected { get; }
    event Func<DeviceMessage, Task>? OnMessageReceived;
    event Action<string>? ConnectionStateChanged;
    Task ConnectAsync(string baseUrl, string authorizationCode, string deviceName, string deviceType, string tenant = "default");
    Task<Guid> SendMessageAsync(Guid conversationId, string content, string messageType = "Text");
    Task DisconnectAsync();
}