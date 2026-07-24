using Acme.DeviceConnect.Domain;
using System;
using System.Threading.Tasks;

namespace Acme.DeviceConnect.Application;

public class DeviceChatService : IDeviceChatClient
{
    private readonly IDeviceChatClient _chatClient;

    public DeviceChatService(IDeviceChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public bool IsConnected => _chatClient.IsConnected;

    public event Func<DeviceMessage, Task>? OnMessageReceived
    {
        add => _chatClient.OnMessageReceived += value;
        remove => _chatClient.OnMessageReceived -= value;
    }

    public event Action<string>? ConnectionStateChanged
    {
        add => _chatClient.ConnectionStateChanged += value;
        remove => _chatClient.ConnectionStateChanged -= value;
    }

    public Task ConnectAsync(string baseUrl, string authorizationCode, string deviceName, string deviceType, string tenant = "default")
    {
        return _chatClient.ConnectAsync(baseUrl, authorizationCode, deviceName, deviceType, tenant);
    }

    public Task<Guid> SendMessageAsync(Guid conversationId, string content, string messageType = "Text")
    {
        return _chatClient.SendMessageAsync(conversationId, content, messageType);
    }

    public Task DisconnectAsync()
    {
        return _chatClient.DisconnectAsync();
    }
}