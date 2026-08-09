using System;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Application.Contracts.DTOs.AgentConfig;

public class LlmConfigDto : EntityDto<Guid>
{
    public string Name { get; set; } = string.Empty;
    public string ModelName { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public float Temperature { get; set; } = 0.5f;
    public bool IsDefault { get; set; } = false;
    /// <summary>是否为系统级配置。系统配置所有用户只读可见，用户配置仅创建者可编辑。</summary>
    public bool IsSystem { get; set; } = false;
    public Guid? CreatorId { get; set; }
    public string? CreatorUserName { get; set; }
}

public class LlmConfigCreateDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ModelName { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public float Temperature { get; set; } = 0.5f;
    public bool IsDefault { get; set; } = false;
    /// <summary>是否为系统级配置。服务端根据调用方法强制设置，客户端传值被忽略。</summary>
    public bool IsSystem { get; set; } = false;
}
