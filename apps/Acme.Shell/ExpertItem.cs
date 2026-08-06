using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>专家（Agent）卡片行模型。</summary>
public sealed partial class ExpertItem : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "普通对话";
    [ObservableProperty] private string _systemPrompt = string.Empty;

    /// <summary>大脑引擎："Llm"（默认）|"Rule"（确定性规则，笔记 body 为规则程序 YAML）。空=Llm。</summary>
    [ObservableProperty] private string _engine = string.Empty;

    /// <summary>笔记节点类型：Agent（普通 agent）|Rule（规则 agent）。目录/规则 tab 按此过滤；默认 Agent。</summary>
    [ObservableProperty] private NoteNodeType _nodeType = NoteNodeType.Agent;
    [ObservableProperty] private string _modelId = string.Empty;
    [ObservableProperty] private string _modelName = string.Empty;
    [ObservableProperty] private List<string> _tools = new();
    /// <summary>按工具名覆盖审批等级（"Safe"/"Confirm"/"Dangerous"）；专家编辑页可调。</summary>
    [ObservableProperty] private Dictionary<string, string> _toolApprovals = new();
    /// <summary>勾选启用的 skill 名（空=启用 skills 时用全部）。</summary>
    [ObservableProperty] private List<string> _skills = new();
    [ObservableProperty] private bool _isSummoned;

    /// <summary>记忆模式：SlidingWindow=滑动窗口, Full=全量, Off=关闭。</summary>
    [ObservableProperty] private AgentMemoryMode _memory = AgentMemoryMode.SlidingWindow;

    /// <summary>滑动窗口最大历史条数。</summary>
    [ObservableProperty] private int _maxHistory = 20;

    /// <summary>来源："Local"|"Marketplace"。</summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>市场云端 Id（null=本地自建）。</summary>
    [ObservableProperty] private Guid? _marketplaceId;

    /// <summary>市场有更新（云端 LastModifiedAt 比本地新）。</summary>
    [ObservableProperty] private bool _hasUpdate;

    // —— 采样 / 输出参数。
    [ObservableProperty] private double? _topP;
    [ObservableProperty] private int? _maxOutputTokens;
    [ObservableProperty] private AgentResponseFormat _responseFormat = AgentResponseFormat.Default;

    // —— HarnessAgent 扩展能力开关。
    [ObservableProperty] private bool _enableWebSearch;
    [ObservableProperty] private bool _enableTodoProvider;
    [ObservableProperty] private bool _requireToolApproval;
    [ObservableProperty] private bool _enableAgentModeProvider;
    [ObservableProperty] private bool _enableFileMemory;
    [ObservableProperty] private bool _enableAgentSkills;
    [ObservableProperty] private bool _enableOpenTelemetry;
    [ObservableProperty] private bool _enableLongTermMemory;

    public string DisplayIcon => NodeType == NoteNodeType.Rule ? "⚙️" : "🤖";
    /// <summary>来源徽章：本地 / 市场。</summary>
    public string SourceBadge => string.Equals(Source, "Marketplace", StringComparison.OrdinalIgnoreCase) ? "市场" : "本地";
    /// <summary>是否市场召唤的 agent。</summary>
    public bool IsMarketplace => MarketplaceId is not null;

    /// <summary>记忆模式显示文本。</summary>
    public string MemoryLabel => Memory switch
    {
        AgentMemoryMode.SlidingWindow => "滑动窗口",
        AgentMemoryMode.Full => "全量记忆",
        AgentMemoryMode.Off => "关闭",
        _ => Memory.ToString(),
    };
}
