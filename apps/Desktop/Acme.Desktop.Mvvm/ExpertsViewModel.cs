using System.Collections.ObjectModel;
using Aether.AI.Core.Agents;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
// Core 与 Notes.Domain 各有一个平行 AgentMemoryMode 枚举（适配器在 ToAgentDefinition 处映射）；
// 此 VM 构建 CustomAgentDefinition（Notes 域），故别名钉到 Notes 域枚举，消解歧义。
using AgentMemoryMode = Acme.Notes.Domain.Agents.AgentMemoryMode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>专家页面 ViewModel：专家卡片列表（供聊天下拉 + labs 编辑保存用）+ 连接器面板。
/// labs 知识对象页承接 agent/rule/workflow/MCP/技能 的创建+编辑后，专家页仅留连接器面板
/// （MCP/技能管理在资料库 mcp-servers / skills 根目录）。UI 无关。</summary>
public partial class ExpertsViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly IUiDispatcher _dispatcher;
    private readonly ICustomAgentEvalRunner? _evalRunner;
    private readonly IReportDialogService? _reportDialog;

    public ExpertsViewModel(ICustomAgentCatalog catalog, ICustomAgentRunner runner, IUiDispatcher dispatcher,
        McpViewModel mcp, IConnectorViewModel? connector = null,
        ICustomAgentEvalRunner? evalRunner = null, IReportDialogService? reportDialog = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        Connector = connector;
        // 评测可选注入（手机宿主不注册 → 编辑器隐藏评测按钮）
        _evalRunner = evalRunner;
        _reportDialog = reportDialog;
    }

    public ObservableCollection<ExpertItem> Experts { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>宿主注入的连接器 VM（null 则无连接器面板）。</summary>
    public IConnectorViewModel? Connector { get; }

    public bool HasConnector => Connector is not null;

    /// <summary>MCP 工具服务器 VM（agent 编辑器 MCP 区用——列 server 供勾选；MCP 管理面板在资料库 mcp-servers 根目录）。</summary>
    public McpViewModel Mcp { get; }

    /// <summary>可选目标集合变更（新建/删除/catalog reload）时触发。ChatWorkspaceViewModel 订阅刷新统一下拉池。</summary>
    public event Action? TargetsChanged;

    /// <summary>外部触发目标集合变更通知（catalog Changed reload 后调用，让聊天下拉框刷新新增项）。</summary>
    internal void NotifyTargetsChanged() => TargetsChanged?.Invoke();

    /// <summary>从目录加载所有自定义 Agent + 刷新连接器状态。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await _catalog.ListAsync();
            var modelNames = _catalog.GetAvailableModels().ToDictionary(m => m.Id, m => m.Name);

            await _dispatcher.InvokeAsync(() =>
            {
                Experts.Clear();
                foreach (var def in all)
                {
                    // 仅普通 agent（NodeType==Agent）；规则 agent 由 RulesViewModel 独立加载。
                    if (def.NodeType != NoteNodeType.Agent) continue;
                    var item = new ExpertItem
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Description = def.Description,
                        Category = string.IsNullOrWhiteSpace(def.Category) ? "普通对话" : def.Category,
                        SystemPrompt = def.SystemPrompt,
                        Engine = def.Engine,
                        NodeType = def.NodeType,
                        ModelId = def.ModelId,
                        ModelName = modelNames.TryGetValue(def.ModelId ?? string.Empty, out var mn) ? mn : string.Empty,
                        Tools = def.Tools.ToList(),
                        ToolApprovals = def.ToolApprovals ?? new Dictionary<string, string>(),
                        AutoApprovalRules = def.AutoApprovalRules?.ToList() ?? new List<string>(),
                        Memory = def.Memory,
                        MaxHistory = def.MaxHistory,
                        TopP = def.TopP,
                        MaxOutputTokens = def.MaxOutputTokens,
                        ResponseFormat = def.ResponseFormat,
                        EnableWebSearch = def.EnableWebSearch,
                        EnableTodoProvider = def.EnableTodoProvider,
                        RequireToolApproval = def.RequireToolApproval,
                        EnableAgentModeProvider = def.EnableAgentModeProvider,
                        EnableFileMemory = def.EnableFileMemory,
                        EnableOpenTelemetry = def.EnableOpenTelemetry,
                        EnableLongTermMemory = def.EnableLongTermMemory,
                        BackgroundAgents = def.BackgroundAgents?.ToList() ?? new List<string>(),
                        Source = def.Source,
                        MarketplaceId = def.MarketplaceId,
                    };
                    Experts.Add(item);
                }
            });
            // 列表内容已（重新）填充 → 通知聊天下拉刷新。初始 LoadAsync 也发：若加载晚于 Chat 初始化
            // （时序竞态/慢加载），RestoreSelectedTargetAsync 的池快照会缺 agent——靠此通知自愈补上
            // （RefreshChatTargetItems 按 (Id,Kind) 静默保留选中，不会清空会话）。
            TargetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }

        // 加载 MCP server 列表（编辑器 MCP 区用；MCP 连接是异步的，此处取当前快照）。
        try { await Mcp.LoadAsync(); } catch { }

        // 刷新连接器卡状态（原 tab 切换刷新——tab 去除后改在 LoadAsync 即启动/catalog 变更时刷新；
        // 运行时连接状态由各卡 LogReceived 事件订阅实时更新）。
        Connector?.RefreshAsync().SafeFireAndForget();
    }

    /// <summary>保存专家（新增或更新）。labs 知识对象页编辑器保存经此落库。</summary>
    public async Task<(bool Success, string Error)> SaveAsync(ExpertItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name))
            return (false, "名称不能为空");

        try
        {
            var existing = Experts.FirstOrDefault(e => e.Id == item.Id);
            var def = new CustomAgentDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "普通对话" : item.Category,
                SystemPrompt = item.SystemPrompt,
                Engine = item.Engine,
                NodeType = item.NodeType,
                ModelId = item.ModelId ?? string.Empty,
                Tools = item.Tools,
                ToolApprovals = item.ToolApprovals ?? new Dictionary<string, string>(),
                AutoApprovalRules = item.AutoApprovalRules?.ToList() ?? new List<string>(),
                Memory = item.Memory,
                MaxHistory = item.MaxHistory,
                TopP = item.TopP,
                MaxOutputTokens = item.MaxOutputTokens,
                ResponseFormat = item.ResponseFormat,
                EnableWebSearch = item.EnableWebSearch,
                EnableTodoProvider = item.EnableTodoProvider,
                RequireToolApproval = item.RequireToolApproval,
                EnableAgentModeProvider = item.EnableAgentModeProvider,
                EnableFileMemory = item.EnableFileMemory,
                EnableOpenTelemetry = item.EnableOpenTelemetry,
                EnableLongTermMemory = item.EnableLongTermMemory,
                BackgroundAgents = item.BackgroundAgents?.ToList() ?? new List<string>(),
            };

            await _catalog.SaveAsync(def);
            // 配置变更后丢弃运行时缓存的旧 agent
            _runner.Invalidate(item.Id);

            if (existing is not null)
            {
                // 编辑：在原 ExpertItem 上更新属性（不重建列表）
                existing.Name = item.Name;
                existing.Description = item.Description;
                existing.Category = def.Category;
                existing.SystemPrompt = item.SystemPrompt;
                existing.Engine = item.Engine;
                existing.NodeType = item.NodeType;
                existing.ModelId = item.ModelId;
                existing.Tools = item.Tools;
                existing.ToolApprovals = item.ToolApprovals;
                existing.AutoApprovalRules = item.AutoApprovalRules;
                existing.Memory = item.Memory;
                existing.MaxHistory = item.MaxHistory;
                existing.TopP = item.TopP;
                existing.MaxOutputTokens = item.MaxOutputTokens;
                existing.ResponseFormat = item.ResponseFormat;
                existing.RequireToolApproval = item.RequireToolApproval;
                existing.EnableWebSearch = item.EnableWebSearch;
                existing.EnableTodoProvider = item.EnableTodoProvider;
                existing.EnableAgentModeProvider = item.EnableAgentModeProvider;
                existing.EnableFileMemory = item.EnableFileMemory;
                existing.EnableOpenTelemetry = item.EnableOpenTelemetry;
                existing.EnableLongTermMemory = item.EnableLongTermMemory;
                existing.BackgroundAgents = item.BackgroundAgents?.ToList() ?? new List<string>();
            }
            else
            {
                // 新建：加载到列表
                await LoadAsync();
            }

            StatusMessage = existing == null ? $"已创建：{item.Name}" : "已保存";
            TargetsChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"保存失败：{ex.Message}");
        }
    }

    /// <summary>创建编辑弹窗 ViewModel（从目录加载可用工具/技能/模型 + MCP server 工具）。
    /// labs 知识对象页 agent 表单编辑器经此构建编辑 VM。</summary>
    public ExpertEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        var models = _catalog.GetAvailableModels();
        // 后台 Agent 白名单候选：目录里其他普通 LLM agent 名（排除正在编辑的自己，防 self-delegate）。
        var selfName = existing?.Name;
        var candidates = Experts
            .Where(e => e.NodeType == NoteNodeType.Agent && !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e.Name.Trim())
            .Where(n => !string.Equals(n, selfName?.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ExpertEditViewModel(existing, tools, models, Mcp.Servers.ToList(), _catalog.CurrentHost, candidates,
            _evalRunner, _reportDialog);
    }
}
