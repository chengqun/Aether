using System;

namespace Acme.Securities.Application;

public class SecurityDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string BaseCode { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
