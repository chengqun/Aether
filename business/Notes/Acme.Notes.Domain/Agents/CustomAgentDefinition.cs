namespace Acme.Notes.Domain.Agents;

/// <summary>
/// UI 无关的自定义 Agent 定义（目录数据模型）。持久化为 Notes 的
/// <c>NoteNodeType.Agent</c> 笔记（<c>AgentSourceParser</c> YAML 格式）。
/// 与平台运行时 <c>AgentDefinition</c> 的映射由 <c>ICustomAgentRunner</c> 完成。
/// </summary>
public sealed class CustomAgentDefinition
{
    /// <summary>稳定标识（对应 Note 的 Id）。</summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "普通对话";

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 模型标识（opaque）：桌面端存 LLM 配置的 Id；空表示用宿主默认模型。
    /// 由 <c>IChatClientFactory</c> 解析为具体的 <c>IChatClient</c>。
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>勾选启用的工具名（对应 ToolRegistry 中的工具）。</summary>
    public IReadOnlyList<string> Tools { get; set; } = Array.Empty<string>();

    /// <summary>按工具名覆盖审批等级（"Safe"/"Confirm"/"Dangerous"）；未列入取工具声明等级。专家编辑页可调。</summary>
    public Dictionary<string, string> ToolApprovals { get; set; } = new();

    /// <summary>勾选启用的 skill 名（对应 SkillRegistry 中的 skill；空=启用 skills 时用全部）。</summary>
    public IReadOnlyList<string> Skills { get; set; } = Array.Empty<string>();

    /// <summary>记忆模式（当前未由运行时强制，仅持久化；见计划缺口）。</summary>
    public AgentMemoryMode Memory { get; set; } = AgentMemoryMode.SlidingWindow;

    public int MaxHistory { get; set; } = 20;

    /// <summary>来源："Local"（本地自建）|"Marketplace"（从市场召唤）。空=本地。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>市场 agent 的云端 Id（召唤来源 / 发布结果）。空=未关联市场。</summary>
    public Guid? MarketplaceId { get; set; }

    /// <summary>上次同步市场时的云端 LastModifiedAt（用于检测更新）。</summary>
    public DateTimeOffset? MarketplaceSyncedAt { get; set; }

    /// <summary>市场原名（本地 Name 会加" [市场]"后缀，此字段保留原名）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    // —— 采样 / 输出参数（可空=沿用 provider 默认）。映射到平台 AgentDefinition。
    public double? TopP { get; set; }
    public int? MaxOutputTokens { get; set; }
    public AgentResponseFormat ResponseFormat { get; set; } = AgentResponseFormat.Default;

    // —— HarnessAgent 扩展能力开关（默认全 false = 全部禁用）。
    public bool EnableWebSearch { get; set; }
    public bool EnableTodoProvider { get; set; }
    /// <summary>开启后该 agent 的工具调用前需经 UI 审批（包成 ApprovalRequiredAIFunction）。</summary>
    public bool RequireToolApproval { get; set; }
    public bool EnableAgentModeProvider { get; set; }
    public bool EnableFileMemory { get; set; }
    public bool EnableAgentSkills { get; set; }
    public bool EnableOpenTelemetry { get; set; }
}
