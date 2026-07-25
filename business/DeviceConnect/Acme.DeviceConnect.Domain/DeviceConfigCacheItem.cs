namespace Acme.DeviceConnect.Domain;

public class DeviceConfigCacheItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServerUrl { get; set; } = string.Empty;
    public string AuthorizationCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = "桌面助手";
    public string DeviceType { get; set; } = "desktop";
    public string Tenant { get; set; } = "default";
    public bool AutoConnect { get; set; } = false;
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;

    public void UpdateFrom(DeviceConfig config)
    {
        ServerUrl = config.ServerUrl;
        AuthorizationCode = config.AuthorizationCode;
        DeviceName = config.DeviceName;
        DeviceType = config.DeviceType;
        Tenant = config.Tenant;
    }

    public void ApplyTo(DeviceConfig config)
    {
        config.ServerUrl = ServerUrl;
        config.AuthorizationCode = AuthorizationCode;
        config.DeviceName = DeviceName;
        config.DeviceType = DeviceType;
        config.Tenant = Tenant;
    }
}