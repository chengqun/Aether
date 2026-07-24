namespace Acme.DeviceManagement.Domain;

public class MyDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string AuthorizationCode { get; set; } = string.Empty;
    public bool HasRelationship { get; set; }
}