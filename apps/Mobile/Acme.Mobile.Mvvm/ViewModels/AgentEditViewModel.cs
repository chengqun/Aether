using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Llm.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using ICustomAgentRunner = Aether.AI.Core.Agents.ICustomAgentRunner;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// Agent 新建/编辑表单 VM（AI 设置页「管理 AI 助手」入口，配置侧——与聊天侧 {@link AgentContactDetailViewModel} 分离，
/// 对应桌面端「连接器/专家=配置区、聊天页=聊天区」的套路）。支持两种平铺 NodeType：
/// <see cref="NoteNodeType.Agent"/>（LLM 智能体，<c>AgentSourceParser</c>）与 <see cref="NoteNodeType.Rule"/>
/// （规则大脑，<c>RuleSourceParser</c>）。Workflow 是另一套 schema（participants/handoffs JSON），CodeRule 需
/// Roslyn（手机跳过），此表单一律不覆盖——它们只读展示。
///
/// 数据真相源与桌面端同一套：<see cref="ICustomAgentCatalog.SaveAsync"/> 直接写 <tenantDir>/labs/* 文件
/// （agents/*.agent.md / rules/*.rule.yaml），两端天然同步。字段完整对齐 <see cref="CustomAgentDefinition"/>。
/// 编辑态经导航参数 <c>agentId</c> 定位；新建态 <c>agentId</c> 缺省 → <see cref="Id"/> 留 Guid.Empty，
/// 保存时赋新 Guid（<c>DirectoryAgentCatalog.SaveAsync</c> 按 NodeType 分发到 parser）。
/// </summary>
public partial class AgentEditViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly ICustomAgentRunner? _agentRunner;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly IEventBusService? _eventBusService;
    private readonly IToastService? _toastService;

    /// <summary>编辑中的定义（新建时为新鲜的空定义）。</summary>
    private CustomAgentDefinition? _agentDef;
    /// <summary>是否为编辑态（true）vs 新建态（false）。</summary>
    private bool _isEditing;

    // —— 基础信息 ——
    [ObservableProperty]
    private string _agentName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    /// <summary>类型 Picker 的选中索引（0=智能体 Agent=1，1=规则 Rule=4）。对应 <see cref="NodeTypeLabels"/>。</summary>
    [ObservableProperty]
    private int _nodeTypeIndex;

    /// <summary>是否规则类型（NodeType==Rule，区别于智能体）。决定提示词编辑区的语义（规则程序 YAML vs agent 提示词）。</summary>
    public bool IsRule => NodeTypeIndex == 1;

    partial void OnNodeTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsRule));
    }

    [ObservableProperty]
    private string _category = "普通对话";

    // —— 记忆 / 采样参数 ——
    [ObservableProperty]
    private int _memoryMode = (int)AgentMemoryMode.SlidingWindow;

    [ObservableProperty]
    private int _maxHistory = 20;

    [ObservableProperty]
    private string _topP = string.Empty;

    [ObservableProperty]
    private string _maxOutputTokens = string.Empty;

    [ObservableProperty]
    private int _responseFormat = (int)AgentResponseFormat.Default;

    // —— HarnessAgent provider 开关 ——
    [ObservableProperty]
    private bool _enableWebSearch;

    [ObservableProperty]
    private bool _enableTodoProvider;

    [ObservableProperty]
    private bool _requireToolApproval;

    [ObservableProperty]
    private bool _enableAgentModeProvider;

    [ObservableProperty]
    private bool _enableFileMemory;

    [ObservableProperty]
    private bool _enableOpenTelemetry;

    [ObservableProperty]
    private bool _enableLongTermMemory;

    // —— 工具勾选 ——
    [ObservableProperty]
    private ObservableCollection<ToolCategoryGroup> _toolCategories = new();

    // —— LLM 模型下拉（复用 AgentContactDetailViewModel 的 Guid 语义：ModelId 存 LlmConfig.Id 字符串） ——
    [ObservableProperty]
    private ObservableCollection<LlmConfigPickerItem> _availableLlmConfigs = new();

    [ObservableProperty]
    private Guid? _selectedLlmConfigId;

    [ObservableProperty]
    private LlmConfigPickerItem? _selectedLlmConfigItem;

    partial void OnSelectedLlmConfigItemChanged(LlmConfigPickerItem? value)
    {
        if (value == null) return;
        if (value.Id == _selectedLlmConfigId) return;
        SelectedLlmConfigId = value.Id;
    }

    // —— 状态 ——
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>导航请求事件（保存后返回等）。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>类型 Picker 的 Display 列表（顺序对应 NodeTypeIndex：0=智能体 1=规则）。</summary>
    public IReadOnlyList<string> NodeTypeLabels { get; } = new[] { "智能体", "规则" };

    /// <summary>记忆模式 Picker 的 Display 列表（顺序与 AgentMemoryMode 枚举值 0..3 对齐）。</summary>
    public IReadOnlyList<string> MemoryModeLabels { get; } = new[] { "滑动窗口", "完整", "关闭", "摘要压缩" };

    /// <summary>响应格式 Picker 的 Display 列表（顺序与 AgentResponseFormat 枚举值 0..2 对齐）。</summary>
    public IReadOnlyList<string> ResponseFormatLabels { get; } = new[] { "默认", "文本", "JSON" };

    public AgentEditViewModel(
        ICustomAgentCatalog? agentCatalog = null,
        ICustomAgentRunner? agentRunner = null,
        ILlmConfigService? llmConfigService = null,
        IEventBusService? eventBusService = null,
        IToastService? toastService = null,
        IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _agentCatalog = agentCatalog;
        _agentRunner = agentRunner;
        _llmConfigService = llmConfigService;
        _eventBusService = eventBusService;
        _toastService = toastService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    /// <summary>
    /// 设置导航参数（平台层 ApplyQueryAttributes 转发）。agentId 可缺省=新建态。
    /// </summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("agentId", out var raw))
        {
            var text = raw?.ToString() ?? string.Empty;
            if (Guid.TryParse(text, out var id) && id != Guid.Empty)
            {
                _isEditing = true;
                _agentDefId = id;
            }
        }
    }

    private Guid? _agentDefId;

    public override async Task OnActivatedAsync()
    {
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            CustomAgentDefinition? existing = null;
            List<LlmConfigPickerItem>? llmConfigs = null;

            await Task.Run(async () =>
            {
                if (_isEditing && _agentCatalog != null && _agentDefId.HasValue)
                {
                    existing = await _agentCatalog.GetAsync(_agentDefId.Value);
                }

                if (_llmConfigService != null)
                {
                    var all = await _llmConfigService.GetCachedAsync();
                    if (all != null)
                    {
                        llmConfigs = all.Select(l => new LlmConfigPickerItem(l.Id, l.Name, l.IsSystem, l.IsDefault)).ToList();
                    }
                }
            });

            _agentDef = existing ?? new CustomAgentDefinition
            {
                Id = Guid.Empty,
                NodeType = NoteNodeType.Agent,
                Source = string.Empty,
            };

            // 回填表单字段
            AgentName = _agentDef.Name;
            Description = _agentDef.Description;
            SystemPrompt = _agentDef.SystemPrompt;
            NodeTypeIndex = _agentDef.NodeType == NoteNodeType.Rule ? 1 : 0;
            Category = string.IsNullOrEmpty(_agentDef.Category) ? "普通对话" : _agentDef.Category;
            MemoryMode = (int)_agentDef.Memory;
            MaxHistory = _agentDef.MaxHistory;
            TopP = _agentDef.TopP?.ToString() ?? string.Empty;
            MaxOutputTokens = _agentDef.MaxOutputTokens?.ToString() ?? string.Empty;
            ResponseFormat = (int)_agentDef.ResponseFormat;
            EnableWebSearch = _agentDef.EnableWebSearch;
            EnableTodoProvider = _agentDef.EnableTodoProvider;
            RequireToolApproval = _agentDef.RequireToolApproval;
            EnableAgentModeProvider = _agentDef.EnableAgentModeProvider;
            EnableFileMemory = _agentDef.EnableFileMemory;
            EnableOpenTelemetry = _agentDef.EnableOpenTelemetry;
            EnableLongTermMemory = _agentDef.EnableLongTermMemory;

            // LLM 下拉
            if (llmConfigs != null)
            {
                AvailableLlmConfigs = new ObservableCollection<LlmConfigPickerItem>(llmConfigs);
            }
            var modelId = _agentDef.ModelId;
            if (Guid.TryParse(modelId, out var llmGuid) && llmGuid != Guid.Empty)
            {
                SelectedLlmConfigId = llmGuid;
                SelectedLlmConfigItem = AvailableLlmConfigs.FirstOrDefault(c => c.Id == llmGuid);
            }

            // 工具勾选（仅 Agent 类型有意义；Rule 用规则程序，工具极少或由 run_rule 隐藏）
            var allTools = _agentCatalog?.GetAvailableTools() ?? Array.Empty<CustomToolMeta>();
            var currentHost = _agentCatalog?.CurrentHost ?? "All";
            var enabledNames = _agentDef.Tools is { Count: > 0 } ? _agentDef.Tools : Array.Empty<string>();

            var categories = new ObservableCollection<ToolCategoryGroup>();
            foreach (var t in allTools)
            {
                var isEnabled = enabledNames.Contains(t.Name);
                var item = new ToolToggleItem(t.Name, t.Description, t.Category, isEnabled, t.HostCapability, currentHost);
                var group = categories.FirstOrDefault(g => g.Category == t.Category);
                if (group == null)
                {
                    group = new ToolCategoryGroup(t.Category);
                    categories.Add(group);
                }
                group.Tools.Add(item);
            }
            ToolCategories = categories;

            OnPropertyChanged(nameof(IsRule));
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>保存（新建=赋新 Guid；编辑=按 Id 覆盖）。写 labs 文件 + 失效运行时缓存 + 广播刷新。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_agentCatalog == null) return;

        if (string.IsNullOrWhiteSpace(AgentName))
        {
            _ = _toastService?.ShowAsync("请填写名称");
            return;
        }

        IsLoading = true;
        try
        {
            var def = _agentDef ?? new CustomAgentDefinition();
            def.Name = AgentName.Trim();
            def.Description = Description?.Trim() ?? string.Empty;
            def.SystemPrompt = SystemPrompt ?? string.Empty;
            def.NodeType = NodeTypeIndex == 1 ? NoteNodeType.Rule : NoteNodeType.Agent;
            def.Category = string.IsNullOrWhiteSpace(Category) ? "普通对话" : Category.Trim();
            def.Memory = (AgentMemoryMode)MemoryMode;
            def.MaxHistory = MaxHistory;
            def.TopP = double.TryParse(TopP, out var topP) ? topP : null;
            def.MaxOutputTokens = int.TryParse(MaxOutputTokens, out var maxTokens) ? maxTokens : null;
            def.ResponseFormat = (AgentResponseFormat)ResponseFormat;
            def.EnableWebSearch = EnableWebSearch;
            def.EnableTodoProvider = EnableTodoProvider;
            def.RequireToolApproval = RequireToolApproval;
            def.EnableAgentModeProvider = EnableAgentModeProvider;
            def.EnableFileMemory = EnableFileMemory;
            def.EnableOpenTelemetry = EnableOpenTelemetry;
            def.EnableLongTermMemory = EnableLongTermMemory;
            def.ModelId = SelectedLlmConfigId?.ToString() ?? string.Empty;
            def.Tools = ToolCategories.SelectMany(g => g.Tools).Where(t => t.IsEnabled).Select(t => t.Name).ToList();

            if (!_isEditing || def.Id == Guid.Empty)
            {
                def.Id = Guid.NewGuid();
                _isEditing = true;
                _agentDefId = def.Id;
            }

            await Task.Run(() => _agentCatalog.SaveAsync(def));
            _agentDef = def;
            _agentRunner?.Invalidate(def.Id);

            _eventBusService?.Publish(new AgentsChangedEvent(_isEditing ? "edited" : "created"));
            _ = _toastService?.ShowAsync("已保存");

            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
            _ = _toastService?.ShowAsync($"保存失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除（仅编辑态；新建态无删除按钮）。</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_agentCatalog == null) return;
        if (!_isEditing || _agentDefId == null || _agentDefId.Value == Guid.Empty) return;

        try
        {
            await Task.Run(() => _agentCatalog.DeleteAsync(_agentDefId.Value));
            _agentRunner?.Invalidate(_agentDefId.Value);
            _eventBusService?.Publish(new AgentsChangedEvent("deleted"));
            _ = _toastService?.ShowAsync("已删除");

            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
            _ = _toastService?.ShowAsync($"删除失败: {ex.Message}");
        }
    }
}
