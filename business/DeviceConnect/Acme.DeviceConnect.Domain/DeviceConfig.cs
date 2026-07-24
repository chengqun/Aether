namespace Acme.DeviceConnect.Domain;

public class DeviceConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string AuthorizationCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = "桌面助手";
    public string DeviceType { get; set; } = "desktop";
    public string Tenant { get; set; } = "default";
}