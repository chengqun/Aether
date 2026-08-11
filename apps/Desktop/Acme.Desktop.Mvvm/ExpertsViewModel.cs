using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.AppAuth.Application.Auth;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>专家页 tab。</summary>
public enum ExpertsTab { Agent, Rule, Workflow, Connector, Market, Mcp }

/// <summary>专家页面 ViewModel：从自定义 Agent 目录加载，展示为专家卡片。UI 无关。</summary>
public partial class ExpertsViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAgentMarketplaceService _marketplace;
    private readonly IAppAuthService _auth;
    private readonly IAgentSourceParser _agentParser;

    public ExpertsViewModel(ICustomAgentCatalog catalog, ICustomAgentRunner runner, IUiDispatcher dispatcher,
        IAgentMarketplaceService marketplace, IAppAuthService auth, WorkflowsViewModel workflows,
        RulesViewModel rules,
        IAgentSourceParser agentParser,
        McpViewModel mcp, IConnectorViewModel? connector = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _marketplace = marketplace ?? throw new ArgumentNullException(nameof(marketplace));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        Workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _agentParser = agentParser ?? throw new ArgumentNullException(nameof(agentParser));
        Mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        Connector = connector;
        RefreshLoginStateAsync().SafeFireAndForget();
    }

    /// <summary>是否已登录 App（门控市场 tab 可见 + 市场调用鉴权）。</summary>
    [ObservableProperty] private bool _isLoggedIn;
    public bool CanShowMarket => IsLoggedIn;

    private async Task RefreshLoginStateAsync()
    {
        try { IsLoggedIn = await _auth.IsLoggedInAsync(); }
        catch { IsLoggedIn = false; }
    }

    public ObservableCollection<ExpertItem> Experts { get; } = new();
    public ObservableCollection<ExpertItem> VisibleExperts { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty] private string _selectedCategory = "全部";
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>当前 tab。</summary>
    [ObservableProperty] private ExpertsTab _currentTab = ExpertsTab.Agent;

    /// <summary>宿主注入的连接器 VM（null 则无连接器 tab）。</summary>
    public IConnectorViewModel? Connector { get; }

    /// <summary>工作流 VM（专家团 tab）。</summary>
    public WorkflowsViewModel Workflows { get; }

    /// <summary>规则 VM（规则 tab）。</summary>
    public RulesViewModel Rules { get; }

    /// <summary>MCP 工具服务器 VM（MCP tab）。</summary>
    public McpViewModel Mcp { get; }

    public bool IsAgentTab => CurrentTab == ExpertsTab.Agent;
    public bool IsRuleTab => CurrentTab == ExpertsTab.Rule;

    public bool IsWorkflowTab => CurrentTab == ExpertsTab.Workflow;
    public bool IsConnectorTab => CurrentTab == ExpertsTab.Connector;
    public bool IsMarketTab => CurrentTab == ExpertsTab.Market;
    public bool IsMcpTab => CurrentTab == ExpertsTab.Mcp;
    public bool HasConnector => Connector is not null;

    partial void OnCurrentTabChanged(ExpertsTab value)
    {
        OnPropertyChanged(nameof(IsAgentTab));
        OnPropertyChanged(nameof(IsRuleTab));
        OnPropertyChanged(nameof(IsWorkflowTab));
        OnPropertyChanged(nameof(IsConnectorTab));
        OnPropertyChanged(nameof(IsMarketTab));
        OnPropertyChanged(nameof(IsMcpTab));
        if (value == ExpertsTab.Rule)
            Rules.LoadAsync().SafeFireAndForget();
        if (value == ExpertsTab.Connector && Connector is not null)
            Connector.RefreshAsync().SafeFireAndForget();
        if (value == ExpertsTab.Market)
        {
            RefreshLoginStateAsync().SafeFireAndForget();
            LoadMarketplaceAsync().SafeFireAndForget();
        }
        if (value == ExpertsTab.Mcp)
            Mcp.LoadAsync().SafeFireAndForget();
    }

    [RelayCommand] private void ShowAgentTab() => CurrentTab = ExpertsTab.Agent;
    [RelayCommand] private void ShowRuleTab() => CurrentTab = ExpertsTab.Rule;
    [RelayCommand] private void ShowWorkflowTab() => CurrentTab = ExpertsTab.Workflow;
    [RelayCommand] private void ShowConnectorTab() { if (HasConnector) CurrentTab = ExpertsTab.Connector; }
    [RelayCommand] private void ShowMarketTab() => CurrentTab = ExpertsTab.Market;
    [RelayCommand] private void ShowMcpTab() => CurrentTab = ExpertsTab.Mcp;

    /// <summary>召唤变化事件（聊天页订阅）。</summary>
    public event Action? SummonChanged;

    /// <summary>从目录加载所有自定义 Agent。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await _catalog.ListAsync();
            var modelNames = _catalog.GetAvailableModels().ToDictionary(m => m.Id, m => m.Name);

            await _dispatcher.InvokeAsync(() =>
            {
                // 保留召唤状态：NotesChanged 等触发 reload 时按 Id 迁移 IsSummoned 到新实例。
                var prevSummoned = Experts.Where(e => e.IsSummoned).Select(e => e.Id).ToHashSet();
                Experts.Clear();
                var catSet = new List<string>();

                foreach (var def in all)
                {
                    // 专家 tab 只列普通 agent（NodeType==Agent）；规则 agent 在「规则」tab 独立展示。
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
                        Skills = def.Skills.ToList(),
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
                    if (prevSummoned.Contains(item.Id)) item.IsSummoned = true;
                    Experts.Add(item);

                    var cat = item.Category;
                    if (!catSet.Contains(cat)) catSet.Add(cat);
                }

                Categories.Clear();
                Categories.Add("全部");
                foreach (var c in catSet.OrderBy(c => c)) Categories.Add(c);
                if (!Categories.Contains(SelectedCategory)) SelectedCategory = "全部";

                RefreshVisibleExperts();
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }

        // 加载 MCP server 列表（编辑器 MCP 区用；MCP 连接是异步的，此处取当前快照）。
        try { await Mcp.LoadAsync(); } catch { }
    }

    private void RefreshVisibleExperts()
    {
        VisibleExperts.Clear();
        var filtered = SelectedCategory == "全部"
            ? Experts
            : Experts.Where(e => e.Category == SelectedCategory);
        foreach (var e in filtered) VisibleExperts.Add(e);
    }

    partial void OnSelectedCategoryChanged(string value) => RefreshVisibleExperts();

    /// <summary>创建新专家（仅在用户点保存后才落库）。</summary>
    public ExpertItem CreateNew()
    {
        return new ExpertItem
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Description = string.Empty,
            Category = "普通对话",
            SystemPrompt = string.Empty,
            Tools = new List<string>(),
            Memory = AgentMemoryMode.SlidingWindow,
            MaxHistory = 20,
        };
    }

    /// <summary>保存专家（新增或更新）。</summary>
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
                Skills = item.Skills,
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
                // 编辑：在原 ExpertItem 上更新属性（不重建，IsSummoned 不丢，无需重新召唤）
                existing.Name = item.Name;
                existing.Description = item.Description;
                existing.Category = string.IsNullOrWhiteSpace(item.Category) ? "普通对话" : item.Category;
                existing.SystemPrompt = item.SystemPrompt;
                existing.Engine = item.Engine;
                existing.NodeType = item.NodeType;
                existing.ModelId = item.ModelId;
                existing.Tools = item.Tools;
                existing.ToolApprovals = item.ToolApprovals;
                existing.AutoApprovalRules = item.AutoApprovalRules;
                existing.Skills = item.Skills;
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
            SummonChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteExpertAsync(ExpertItem item)
    {
        if (item == null) return;
        try
        {
            await _catalog.DeleteAsync(item.Id);
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = $"已删除：{item.Name}";
            SummonChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleSummon(ExpertItem item)
    {
        if (item == null) return;
        item.IsSummoned = !item.IsSummoned;
        StatusMessage = item.IsSummoned ? $"已召唤：{item.Name}" : $"已取消召唤：{item.Name}";
        SummonChanged?.Invoke();
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        SelectedCategory = category;
    }

    /// <summary>获取已召唤的专家列表。</summary>
    public List<ExpertItem> GetSummonedExperts() => Experts.Where(e => e.IsSummoned).ToList();

    /// <summary>创建编辑弹窗 ViewModel（从目录加载可用工具/技能/模型 + MCP server 工具）。</summary>
    public ExpertEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        var skills = _catalog.GetAvailableSkills();
        var models = _catalog.GetAvailableModels();
        return new ExpertEditViewModel(existing, tools, skills, models, Mcp.Servers.ToList());
    }

    // —— 市场 ——
    public ObservableCollection<MarketplaceAgentDto> MarketplaceAgents { get; } = new();

    [ObservableProperty] private bool _isMarketLoading;
    [ObservableProperty] private string _marketStatus = string.Empty;

    /// <summary>切换到市场 tab 时懒加载列表。</summary>
    public async Task LoadMarketplaceAsync()
    {
        if (IsMarketLoading) return;
        IsMarketLoading = true;
        MarketStatus = "加载中…";
        try
        {
            var list = await _marketplace.GetMarketplaceAsync();
            if (list is null)
            {
                MarketStatus = "市场加载失败：未登录 App 或服务端未就绪";
                return;
            }
            var local = await _catalog.ListAsync();
            var byMarket = local.Where(d => d.MarketplaceId.HasValue)
                .ToDictionary(d => d.MarketplaceId!.Value, d => d);

            await _dispatcher.InvokeAsync(() =>
            {
                MarketplaceAgents.Clear();
                foreach (var m in list)
                {
                    m.IsSummoned = byMarket.ContainsKey(m.Id);
                    m.HasUpdate = m.IsSummoned
                        && byMarket[m.Id].MarketplaceSyncedAt.HasValue
                        && m.LastModifiedAt > byMarket[m.Id].MarketplaceSyncedAt!.Value
                        && !string.Equals(byMarket[m.Id].Source, "Local", StringComparison.OrdinalIgnoreCase);
                    MarketplaceAgents.Add(m);
                }
                MarketStatus = list.Count == 0 ? "市场暂无 agent" : string.Empty;
            });
        }
        catch (Exception ex)
        {
            MarketStatus = $"市场加载失败：{ex.Message}";
        }
        finally { IsMarketLoading = false; }
    }

    /// <summary>召唤市场 agent 到本地。</summary>
    public async Task<(bool Success, string Error)> SummonFromMarketplaceAsync(MarketplaceAgentDto dto)
    {
        try
        {
            var detail = await _marketplace.GetAgentDetailAsync(dto.Id);
            if (detail is null) return (false, "取详情失败");

            _agentParser.TryParse(detail.Content, out var parsed, out _);
            var def = parsed ?? new CustomAgentDefinition();
            def.Id = Guid.NewGuid();
            def.Name = (detail.AgentName ?? "市场 Agent") + " [市场]";
            def.Description = detail.Description;
            def.Category = string.IsNullOrWhiteSpace(detail.Category) ? "普通对话" : detail.Category;
            def.SystemPrompt = parsed?.SystemPrompt ?? detail.Content;
            def.Source = "Marketplace";
            def.MarketplaceId = detail.Id;
            def.MarketplaceSyncedAt = detail.LastModifiedAt;
            def.DisplayName = detail.AgentName;
            await _catalog.SaveAsync(def);
            await LoadAsync();
            // 自动召唤
            var item = Experts.FirstOrDefault(e => e.MarketplaceId == detail.Id);
            if (item is not null && !item.IsSummoned) item.IsSummoned = true;
            SummonChanged?.Invoke();
            await LoadMarketplaceAsync();
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, $"召唤失败：{ex.Message}"); }
    }

    /// <summary>检查市场 agent 更新并拉取最新源文本。</summary>
    public async Task<(bool Success, string Error)> CheckUpdateMarketplaceAsync(MarketplaceAgentDto dto)
    {
        try
        {
            var detail = await _marketplace.GetAgentDetailAsync(dto.Id);
            if (detail is null) return (false, "取详情失败");
            var local = await _catalog.GetByMarketplaceIdAsync(dto.Id);
            if (local is null) return (false, "本地无副本");

            _agentParser.TryParse(detail.Content, out var parsed, out _);
            local.SystemPrompt = parsed?.SystemPrompt ?? detail.Content;
            local.Tools = parsed?.Tools ?? local.Tools;
            local.MarketplaceSyncedAt = detail.LastModifiedAt;
            await _catalog.SaveAsync(local);
            _runner.Invalidate(local.Id);
            await LoadAsync();
            await LoadMarketplaceAsync();
            return (true, "已更新");
        }
        catch (Exception ex) { return (false, $"更新失败：{ex.Message}"); }
    }

    /// <summary>移除市场召唤的本地副本（仅删本地，云端不动）。</summary>
    public async Task<(bool Success, string Error)> RemoveMarketplaceAgentAsync(MarketplaceAgentDto dto)
    {
        try
        {
            var local = await _catalog.GetByMarketplaceIdAsync(dto.Id);
            if (local is null) return (false, "本地无副本");
            await _catalog.DeleteAsync(local.Id);
            _runner.Invalidate(local.Id);
            await LoadAsync();
            SummonChanged?.Invoke();
            await LoadMarketplaceAsync();
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, $"移除失败：{ex.Message}"); }
    }

    /// <summary>发布本地自建 agent 到市场。</summary>
    public async Task<(bool Success, string Error)> PublishToMarketplaceAsync(ExpertItem item)
    {
        if (item is null) return (false, "未选择");
        try
        {
            var def = await _catalog.GetAsync(item.Id);
            if (def is null) return (false, "本地无定义");
            var source = _agentParser.Serialize(def);

            var cloudId = def.MarketplaceId;
            var input = new PublishLocalAgentInputDto
            {
                AgentName = def.Name, Description = def.Description, Category = def.Category,
                Content = source, Kind = "Agent",
            };
            if (cloudId.HasValue)
            {
                var ok = await _marketplace.SyncLocalAsync(cloudId.Value, input);
                if (!ok) return (false, "更新市场失败");
            }
            else
            {
                var newId = await _marketplace.PublishLocalAsync(input);
                if (newId is null) return (false, "发布失败（服务端未就绪？）");
                def.MarketplaceId = newId;
                def.Source = "Local";
                def.MarketplaceSyncedAt = DateTimeOffset.UtcNow;
                await _catalog.SaveAsync(def);
            }
            await LoadAsync();
            return (true, cloudId.HasValue ? "已更新市场" : "已发布到市场");
        }
        catch (Exception ex) { return (false, $"发布失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task SummonFromMarketplace(MarketplaceAgentDto dto)
    {
        var r = await SummonFromMarketplaceAsync(dto);
        StatusMessage = r.Success ? $"已召唤：{dto.AgentName}" : r.Error;
    }

    [RelayCommand]
    private async Task CheckUpdateMarketplace(MarketplaceAgentDto dto)
    {
        var r = await CheckUpdateMarketplaceAsync(dto);
        StatusMessage = r.Success ? (r.Error == "已更新" ? "已更新到最新" : "已是最新") : r.Error;
    }

    [RelayCommand]
    private async Task RemoveMarketplaceAgent(MarketplaceAgentDto dto)
    {
        var r = await RemoveMarketplaceAgentAsync(dto);
        StatusMessage = r.Success ? $"已移除：{dto.AgentName}" : r.Error;
    }

    [RelayCommand]
    private async Task PublishToMarketplace(ExpertItem item)
    {
        var r = await PublishToMarketplaceAsync(item);
        StatusMessage = r.Success ? r.Error : r.Error;
    }
}
