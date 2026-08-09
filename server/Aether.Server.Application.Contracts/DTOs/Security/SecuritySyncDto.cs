using System;
using System.Collections.Generic;

namespace Aether.Server.Application.Contracts.DTOs.Security;

public class SecuritySyncItemDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string BaseCode { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public class SecuritySyncResultDto
{
    public List<SecuritySyncItemDto> Securities { get; set; } = new();
    public DateTime ServerTime { get; set; }
    public bool IsFullSync { get; set; }
}
