namespace Acme.AppUpdate.Domain;

public class CheckUpdateResult
{
    public bool HasUpdate { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}