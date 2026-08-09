using System;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Application.Contracts.DTOs.VersionConfig;

public class VersionConfigDto : EntityDto<Guid>
{
    public AppPlatform Platform { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}

public class VersionConfigCreateDto
{
    public AppPlatform Platform { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}

public class VersionConfigUpdateDto
{
    public AppPlatform Platform { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}

public class VersionConfigPublishDto
{
    public int Platform { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}

public class CheckUpdateInputDto
{
    public AppPlatform Platform { get; set; }
    public int CurrentVersionCode { get; set; }
}

public class CheckUpdateResultDto
{
    public bool HasUpdate { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public string UpdateNote { get; set; } = string.Empty;
    public bool IsForce { get; set; }
}
