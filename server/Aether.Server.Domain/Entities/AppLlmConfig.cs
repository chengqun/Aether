using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// LLM 配置：每个用户可有多条（如 DeepSeek、GPT-4o、本地模型）。
/// 各 Agent 通过本地 NoteNode 源文本中的 llm 字段（按名引用）选择使用哪条 LLM 配置。
/// 多租户：用户级配置 TenantId=用户租户；系统级配置(IsSystem=true) TenantId=null（host 共享，admin 配一次所有租户可用）。
/// </summary>
public class AppLlmConfig : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>多租户：所属租户。系统级配置(IsSystem=true)为 null（host 共享）。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>配置名称，如"DeepSeek"、"GPT-4o"</summary>
    public string Name { get; set; } = string.Empty;

    public string ModelName { get; set; } = "deepseek-v4-flash";

    public string ApiKey { get; set; } = string.Empty;

    public string Endpoint { get; set; } = "https://api.deepseek.com";

    public float Temperature { get; set; } = 0.5f;

    /// <summary>是否为默认配置（每个用户至少一条为 true）</summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>是否为系统级配置。系统配置由管理员管理，所有用户只读可见；用户配置仅创建者可见可改。</summary>
    public bool IsSystem { get; set; } = false;

    public AppLlmConfig() { }

    public AppLlmConfig(
        string name,
        string modelName,
        string apiKey,
        string endpoint,
        float temperature = 0.5f,
        bool isDefault = false,
        bool isSystem = false)
    {
        Name = name;
        ModelName = modelName;
        ApiKey = apiKey;
        Endpoint = endpoint;
        Temperature = temperature;
        IsDefault = isDefault;
        IsSystem = isSystem;
    }
}
