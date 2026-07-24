using System;

namespace Acme.DeviceManagement.Domain;

public class DeviceConfigCacheItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public string AuthorizationCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = "桌面助手";
    public string DeviceType { get; set; } = "desktop";
    public string LastAgentName { get; set; } = string.Empty;
    public string? RecentAgentNamesJson { get; set; }
    public Guid LastLlmId { get; set; }
    public DateTime CachedAt { get; set; }
}