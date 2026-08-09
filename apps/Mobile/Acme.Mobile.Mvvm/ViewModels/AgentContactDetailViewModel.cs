using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// AI 助手详情 VM（通讯录 → AI 助手项 → 详情）。
/// 显示 Agent 的描述 / 工具开关 / LLM 配置选择，并支持发消息 / 删除。
///
/// Aether 适配：旧 AlphaAgent 用 IAgentService+IAgentFactory+IToolRegistry+IAgentSourceService+
/// AgentOptions 五件套；Aether 统一到 <see cref="ICustomAgentCatalog"/>——Agent 的持久化真相源即
/// <see cref="CustomAgentDefinition"/>（Note 笔记），工具列表/LLM 配置都回写它的 Tools/ModelId 字段，
/// SaveAsync 即时落盘 + <see cref="ICustomAgentRunner.Invalidate"/> 刷新运行时缓存。
/// LLM 配置列表仍走 <see cref="ILlmConfigService.GetCachedAsync"/>（内部经 ICurrentUser 解析用户）。
/// </summary>
public partial class AgentContactDetailViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly ICustomAgentRunner? _agentRunner;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly IEventBusService? _eventBusService;

    /// <summary>当前 Agent 的定义 Id（持久化真相源；为空表示未落盘）。</summary>
    private Guid? _agentDefId;
    /// <summary>当前 Agent 的定义（工具/LLM 等编辑回写用）。</summary>
    private CustomAgentDefinition? _agentDef;

    [ObservableProperty]
    private string _agentName = "未知助手";

    [ObservableProperty]
    private string _agentInitial = "?";

    [ObservableProperty]
    private string _description = "暂无描述";

    [ObservableProperty]
    private ObservableCollection<ToolCategoryGroup> _toolCategories = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 是否为工作流类型（无外部工具，显示步骤信息而非工具开关）。
    /// </summary>
    [ObservableProperty]
    private bool _isWorkflow;

    /// <summary>
    /// Agent 类型分类
    /// </summary>
    [ObservableProperty]
    private AgentKind _agentKind;

    /// <summary>
    /// 是否为 LLM Agent 类型
    /// </summary>
    public bool IsAgentKindAgent => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.Agent;

    /// <summary>
    /// 是否为 Workflow 类型
    /// </summary>
    public bool IsAgentKindWorkflow => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.Workflow;

    /// <summary>
    /// 是否为 RuleBased 类型
    /// </summary>
    public bool IsAgentKindRuleBased => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.RuleBased;

    partial void OnAgentKindChanged(AgentKind value)
    {
        OnPropertyChanged(nameof(IsAgentKindAgent));
        OnPropertyChanged(nameof(IsAgentKindWorkflow));
        OnPropertyChanged(nameof(IsAgentKindRuleBased));
    }

    /// <summary>
    /// 工作流的子 Agent 步骤列表（仅 IsWorkflow=true 时使用）。
    /// Aether 工作流的参与者/编排细节在 <c>CustomWorkflowDefinition</c>，本 VM 仅展示概要。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<WorkflowStepItem> _workflowSteps = new();

    /// <summary>
    /// 可选的 LLM 配置列表（系统级 + 用户级）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LlmConfigPickerItem> _availableLlmConfigs = new();

    /// <summary>
    /// 当前选中的 LLM 配置 ID（null 表示使用默认）
    /// </summary>
    [ObservableProperty]
    private Guid? _selectedLlmConfigId;

    /// <summary>
    /// 当前使用的 LLM 配置名称（显示用）
    /// </summary>
    [ObservableProperty]
    private string _currentLlmConfigName = "默认";

    /// <summary>
    /// ComboBox 选中的 LLM 配置项。切换时自动保存到 Agent 定义。
    /// null 表示"使用系统默认"。
    /// </summary>
    [ObservableProperty]
    private LlmConfigPickerItem? _selectedLlmConfigItem;

    partial void OnSelectedLlmConfigItemChanged(LlmConfigPickerItem? value)
    {
        if (value == null) return;
        // 防止初始化时重复触发
        if (value.Id == _selectedLlmConfigId) return;
        _ = ChangeLlmConfigAsync(value.Id);
    }

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public AgentContactDetailViewModel(
        ICustomAgentCatalog? agentCatalog = null,
        ICustomAgentRunner? agentRunner = null,
        ILlmConfigService? llmConfigService = null,
        IUIThreadDispatcher? uiThreadDispatcher = null,
        IEventBusService? eventBusService = null)
    {
        _agentCatalog = agentCatalog;
        _agentRunner = agentRunner;
        _llmConfigService = llmConfigService;
        _uiThreadDispatcher = uiThreadDispatcher;
        _eventBusService = eventBusService;
    }

    /// <summary>
    /// 是否可删除该 Agent（本地配置已落盘、且有配置 Id 时为 true）。
    /// MAUI 的 Agent 全部来自市场召唤（Source=Marketplace），删除后可重新召唤。
    /// </summary>
    [ObservableProperty]
    private bool _canDeleteAgent;

    /// <summary>
    /// 设置导航参数（由平台层调用）
    /// </summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("agentName", out var name))
        {
            AgentName = Uri.UnescapeDataString(name?.ToString() ?? "未知助手");
            AgentInitial = AgentName.Length > 0 ? AgentName[0].ToString() : "?";
        }
    }

    public override async Task OnActivatedAsync()
    {
        await LoadAgentInfoAsync();
    }

    [RelayCommand]
    private async Task LoadAgentInfoAsync()
    {
        try
        {
            // 将所有 I/O 操作（HTTP + SQLite）放到后台线程
            string? description = null;
            List<string>? enabledToolNames = null;
            AgentKind kind = Acme.Mobile.Mvvm.ViewModels.AgentKind.Agent;
            Guid? currentLlmConfigId = null;
            Guid? defId = null;
            CustomAgentDefinition? agentDef = null;
            List<LlmConfigPickerItem>? llmConfigs = null;
            string? currentLlmName = null;

            await Task.Run(async () =>
            {
                if (_agentCatalog != null)
                {
                    var agents = await _agentCatalog.ListAsync();
                    var agent = agents.FirstOrDefault(a => string.Equals(a.Name, AgentName, StringComparison.OrdinalIgnoreCase));
                    if (agent != null)
                    {
                        agentDef = agent;
                        defId = agent.Id;
                        description = !string.IsNullOrEmpty(agent.Description) ? agent.Description : "暂无描述";
                        kind = agent.NodeType switch
                        {
                            NoteNodeType.Workflow => Acme.Mobile.Mvvm.ViewModels.AgentKind.Workflow,
                            NoteNodeType.Rule or NoteNodeType.CodeRule => Acme.Mobile.Mvvm.ViewModels.AgentKind.RuleBased,
                            _ => Acme.Mobile.Mvvm.ViewModels.AgentKind.Agent,
                        };
                        // 工具列表即定义的 Tools 字段（已启用工具）
                        if (agent.Tools is { Count: > 0 })
                            enabledToolNames = agent.Tools.ToList();
                    }
                }

                // LLM 配置列表走 LlmConfigService（内部经 ICurrentUser 解析用户，无需 VM 传 userId）
                if (_llmConfigService != null)
                {
                    var llmAllConfigs = await _llmConfigService.GetCachedAsync();
                    if (llmAllConfigs != null)
                    {
                        llmConfigs = llmAllConfigs.Select(l => new LlmConfigPickerItem(
                            l.Id, l.Name, l.IsSystem, l.IsDefault)).ToList();
                    }

                    // Aether：CustomAgentDefinition.ModelId 存 LlmConfig.Id（字符串）；空=系统默认
                    var modelId = agentDef?.ModelId;
                    if (Guid.TryParse(modelId, out var llmGuid) && llmGuid != Guid.Empty)
                    {
                        currentLlmConfigId = llmGuid;
                        var matched = llmConfigs?.FirstOrDefault(l => l.Id == llmGuid);
                        currentLlmName = matched?.Name ?? modelId;
                    }
                    else
                    {
                        currentLlmName = "默认";
                    }
                }
            });

            // 记录定义 Id + 定义（供工具开关/删除/LLM 切换回写）
            _agentDefId = defId;
            _agentDef = agentDef;

            // 回到主线程更新 UI
            if (description != null)
                Description = description;

            AgentKind = kind;

            // 决定是否可删除（定义已落盘即可删）
            CanDeleteAgent = defId.HasValue && defId.Value != Guid.Empty;

            // 更新 LLM 配置选择 UI
            if (llmConfigs != null)
            {
                AvailableLlmConfigs = new ObservableCollection<LlmConfigPickerItem>(llmConfigs);
            }
            SelectedLlmConfigId = currentLlmConfigId;
            CurrentLlmConfigName = currentLlmName ?? "默认";
            // 设置 ComboBox 选中项（先设 SelectedLlmConfigId 避免重复触发保存）
            SelectedLlmConfigItem = AvailableLlmConfigs.FirstOrDefault(c => c.Id == currentLlmConfigId);

            // 工作流类型：显示步骤信息（无外部工具开关）
            if (kind == Acme.Mobile.Mvvm.ViewModels.AgentKind.Workflow)
            {
                IsWorkflow = true;
                var steps = new ObservableCollection<WorkflowStepItem>
                {
                    // Aether 工作流参与者/编排在 CustomWorkflowDefinition；此处仅展示概要
                    new WorkflowStepItem(1, AgentName, Description, new List<string>())
                };
                WorkflowSteps = steps;
                return;
            }

            IsWorkflow = false;

            // 工具列表取自完整工具池（ICustomAgentCatalog.GetAvailableTools），而非 Agent 当前已启用工具。
            // 否则取消勾选某工具后，下次进入页面该工具会从列表消失、无法再次勾选。
            var allTools = _agentCatalog?.GetAvailableTools() ?? Array.Empty<CustomToolMeta>();

            var categories = new ObservableCollection<ToolCategoryGroup>();

            foreach (var t in allTools)
            {
                var isEnabled = enabledToolNames == null || enabledToolNames.Contains(t.Name);
                var item = new ToolToggleItem(t.Name, t.Description, t.Category, isEnabled);
                item.IsEnabledChanged += OnToolIsEnabledChanged;

                var group = categories.FirstOrDefault(g => g.Category == t.Category);
                if (group == null)
                {
                    group = new ToolCategoryGroup(t.Category);
                    categories.Add(group);
                }
                group.Tools.Add(item);
            }

            ToolCategories = categories;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    private async void OnToolIsEnabledChanged(ToolToggleItem item)
    {
        await SaveToolsConfigAsync();
    }

    private async Task SaveToolsConfigAsync()
    {
        try
        {
            if (_agentDef == null || _agentDefId == null) return;

            var enabledNames = ToolCategories.SelectMany(g => g.Tools).Where(t => t.IsEnabled).Select(t => t.Name).ToList();
            _agentDef.Tools = enabledNames;

            // 回写 Agent 定义（工具列表），即时落盘 + 失效运行时缓存
            await Task.Run(() => _agentCatalog!.SaveAsync(_agentDef));
            _agentRunner?.Invalidate(_agentDefId.Value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentContactDetailVM] 保存工具配置失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task SendMessageAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
            "AgentChatDetailPage",
            new Dictionary<string, object> { ["agentName"] = AgentName }));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除当前 Agent（删 CustomAgentDefinition；市场召唤的可重新召唤）。
    /// </summary>
    [RelayCommand]
    private async Task DeleteAgentAsync()
    {
        if (_agentCatalog == null) return;
        if (_agentDefId == null || _agentDefId.Value == Guid.Empty) return;

        try
        {
            await Task.Run(() => _agentCatalog.DeleteAsync(_agentDefId.Value));
            _agentRunner?.Invalidate(_agentDefId.Value);

            // 通知通讯录刷新 AI 助手分组（移除已删除的 Agent）
            _eventBusService?.Publish(new AgentsChangedEvent("deleted"));

            StatusMessage = "已删除 Agent";

            // 返回上一页
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentContactDetailVM] 删除 Agent 失败: {ex.Message}");
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 切换 Agent 的 LLM 配置：Aether 用 <c>CustomAgentDefinition.ModelId</c>（LlmConfig.Id 字符串）。
    /// 选择具体配置则写其 Id，选择"默认"（null）则清空 ModelId 走系统默认。
    /// </summary>
    [RelayCommand]
    private async Task ChangeLlmConfigAsync(Guid? llmConfigId)
    {
        if (_agentCatalog == null || _agentDef == null || _agentDefId == null) return;

        try
        {
            _agentDef.ModelId = llmConfigId.HasValue ? llmConfigId.Value.ToString() : string.Empty;
            await Task.Run(() => _agentCatalog.SaveAsync(_agentDef));
            _agentRunner?.Invalidate(_agentDefId.Value);

            // 更新显示名
            var picked = llmConfigId.HasValue
                ? AvailableLlmConfigs.FirstOrDefault(c => c.Id == llmConfigId.Value)
                : null;
            CurrentLlmConfigName = picked?.Name ?? "默认";

            SelectedLlmConfigId = llmConfigId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentContactDetailVM] 切换LLM配置失败: {ex.Message}");
            StatusMessage = $"切换LLM配置失败: {ex.Message}";
        }
    }
}

/// <summary>
/// LLM 配置选择项：用于 Agent 详情页的 LLM 配置下拉选择
/// </summary>
public class LlmConfigPickerItem
{
    public Guid Id { get; }
    public string Name { get; }
    public bool IsSystem { get; }
    public bool IsDefault { get; }

    /// <summary>显示文本：名称 + 标签（系统/默认）</summary>
    public string DisplayName
    {
        get
        {
            var tags = new List<string>();
            if (IsSystem) tags.Add("系统");
            if (IsDefault) tags.Add("默认");
            return tags.Count > 0 ? $"{Name} ({string.Join("/", tags)})" : Name;
        }
    }

    public LlmConfigPickerItem(Guid id, string name, bool isSystem, bool isDefault)
    {
        Id = id;
        Name = name;
        IsSystem = isSystem;
        IsDefault = isDefault;
    }
}
