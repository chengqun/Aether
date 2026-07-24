using System;
using System.Collections.Generic;

namespace Acme.Securities.Application;

public class SecuritySyncItemDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string BaseCode { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public class SecuritySyncResponseDto
{
    public List<SecuritySyncItemDto> Securities { get; set; } = new();
    public DateTime ServerTime { get; set; }
    public bool IsFullSync { get; set; }
}
