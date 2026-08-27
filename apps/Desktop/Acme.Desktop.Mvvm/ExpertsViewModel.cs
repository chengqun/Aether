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

/// <summary>专家页 tab（labs 知识对象页承接 agent/rule/workflow 创建+编辑后，专家页仅留 连接器/MCP/技能 三 tab）。</summary>
public enum ExpertsTab { Connector, Mcp, Skill }

/// <summary>专家页面 ViewModel：专家卡片列表（供聊天下拉 + labs 编辑保存用）。UI 无关。</summary>
public partial class ExpertsViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly IUiDispatcher _dispatcher;

    public ExpertsViewModel(ICustomAgentCatalog catalog, ICustomAgentRunner runner, IUiDispatcher dispatcher,
        McpViewModel mcp, SkillViewModel skills, IConnectorViewModel? connector = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        Skills = skills ?? throw new ArgumentNullException(nameof(skills));
        Connector = connector;
    }

    public ObservableCollection<ExpertItem> Experts { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>当前 tab。</summary>
    [ObservableProperty] private ExpertsTab _currentTab = ExpertsTab.Connector;

    /// <summary>宿主注入的连接器 VM（null 则无连接器 tab）。</summary>
    public IConnectorViewModel? Connector { get; }

    /// <summary>MCP 工具服务器 VM（MCP tab）。</summary>
    public McpViewModel Mcp { get; }

    /// <summary>技能 VM（技能 tab，只读展示已加载 skill：name/category/description + 正文预览）。</summary>
    public SkillViewModel Skills { get; }

    public bool IsConnectorTab => CurrentTab == ExpertsTab.Connector;
    public bool IsMcpTab => CurrentTab == ExpertsTab.Mcp;
    public bool IsSkillTab => CurrentTab == ExpertsTab.Skill;
    public bool HasConnector => Connector is not null;

    partial void OnCurrentTabChanged(ExpertsTab value)
    {
        OnPropertyChanged(nameof(IsConnectorTab));
        OnPropertyChanged(nameof(IsMcpTab));
        OnPropertyChanged(nameof(IsSkillTab));
        if (value == ExpertsTab.Connector && Connector is not null)
            Connector.RefreshAsync().SafeFireAndForget();
        if (value == ExpertsTab.Mcp)
            Mcp.LoadAsync().SafeFireAndForget();
        if (value == ExpertsTab.Skill)
            Skills.LoadAsync().SafeFireAndForget();
    }

    [RelayCommand] private void ShowConnectorTab() { if (HasConnector) CurrentTab = ExpertsTab.Connector; }
    [RelayCommand] private void ShowMcpTab() => CurrentTab = ExpertsTab.Mcp;
    [RelayCommand] private void ShowSkillTab() => CurrentTab = ExpertsTab.Skill;

    /// <summary>可选目标集合变更（新建/删除/catalog reload）时触发。ChatWorkspaceViewModel 订阅刷新统一下拉池。</summary>
    public event Action? TargetsChanged;

    /// <summary>外部触发目标集合变更通知（catalog Changed reload 后调用，让聊天下拉框刷新新增项）。</summary>
    internal void NotifyTargetsChanged() => TargetsChanged?.Invoke();

    /// <summary>从目录加载所有自定义 Agent。</summary>
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
                        Source = def.Source,
                        MarketplaceId = def.MarketplaceId,
                    };
                    Experts.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }

        // 加载 MCP server 列表（编辑器 MCP 区用；MCP 连接是异步的，此处取当前快照）。
        try { await Mcp.LoadAsync(); } catch { }
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
        return new ExpertEditViewModel(existing, tools, models, Mcp.Servers.ToList());
    }
}
