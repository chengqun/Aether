using System;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

public class AppVersionConfig : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public AppPlatform Platform { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }

    public AppVersionConfig() { }

    public AppVersionConfig(
        AppPlatform platform,
        int versionCode,
        string versionName,
        string updateUrl,
        string updateNote,
        bool isForce = false)
    {
        Platform = platform;
        VersionCode = versionCode;
        VersionName = versionName;
        UpdateUrl = updateUrl;
        UpdateNote = updateNote;
        IsForce = isForce;
    }
}
