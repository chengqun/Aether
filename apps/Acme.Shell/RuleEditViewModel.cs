using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>
/// 规则编辑器 ViewModel（独立于 <see cref="ExpertEditViewModel"/>）。规则不用 LLM，故无模型/采样/skill/
/// web-search/todo/agent-mode/file-memory/long-term-memory/telemetry 区——只有基本信息 + 内置工具勾选 +
/// MCP 工具区 + 工具审批 + 规则程序 YAML 框。保存时合并成单个 YAML 文件（<see cref="IRuleSourceParser"/>）。
/// 复用 <see cref="ToolOption"/>/<see cref="ToolCategoryGroup"/>/<see cref="McpServerToolGroup"/>/
/// <see cref="MemoryModeOption"/> 共享类型（定义在 <see cref="ExpertEditViewModel"/>，同 namespace）。
/// </summary>
public sealed partial class RuleEditViewModel : ObservableObject
{
    private readonly NoteNodeType _nodeType;

    /// <param name="nodeType">规则节点类型：<see cref="NoteNodeType.Rule"/>（YAML 规则）或
    /// <see cref="NoteNodeType.CodeRule"/>（C# 代码规则）。两者共用同一表单（基本信息/记忆/工具/审批），
    /// 仅 NodeType、默认类别与默认模板不同。代码规则另经笔记页两栏编辑（规则 tab 卡片无「编辑」按钮）。</param>
    public RuleEditViewModel(ExpertItem? existing, IReadOnlyList<CustomToolMeta> allTools, IReadOnlyList<McpServerStatus>? mcpServers, NoteNodeType nodeType = NoteNodeType.Rule)
    {
        _nodeType = nodeType;
        var selectedSet = new HashSet<string>(existing?.Tools ?? Enumerable.Empty<string>());

        // 按分类分组构建原生工具列表（MCP 工具已在 GetAvailableTools 过滤，单独走 MCP 区）。
        foreach (var meta in allTools)
        {
            var opt = new ToolOption
            {
                Name = meta.Name,
                Description = meta.Description,
                Category = meta.Category,
                DeclaredLevel = meta.ApprovalLevel,
                ApprovalLevel = (existing?.ToolApprovals is not null
                    && existing.ToolApprovals.TryGetValue(meta.Name, out var ov)) ? ov : meta.ApprovalLevel,
                IsSelected = selectedSet.Contains(meta.Name),
            };

            var group = ToolCategories.FirstOrDefault(g => g.Category == meta.Category);
            if (group == null)
            {
                group = new ToolCategoryGroup { Category = meta.Category };
                ToolCategories.Add(group);
            }

            opt.PropertyChanged += (_, _) => { group.RefreshIsAllSelected(); ScheduleAutoSave(); };
            Tools.Add(opt);
            group.Tools.Add(opt);
        }

        // MCP 工具区：按 server 分组（MCP 工具默认 Dangerous）。
        if (mcpServers is not null)
        {
            foreach (var server in mcpServers)
            {
                var grp = new McpServerToolGroup { Name = server.Name, IsConnected = server.IsConnected, Error = server.Error };
                foreach (var tool in server.Tools)
                {
                    var opt = new ToolOption
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Category = "MCP",
                        DeclaredLevel = "Dangerous",
                        ApprovalLevel = (existing?.ToolApprovals is not null
                            && existing.ToolApprovals.TryGetValue(tool.Name, out var ov)) ? ov : "Dangerous",
                        IsSelected = selectedSet.Contains(tool.Name),
                    };
                    Tools.Add(opt);
                    grp.Tools.Add(opt);
                    // MCP 工具选中/权限变化同样触发自动保存
                    opt.PropertyChanged += (_, _) => ScheduleAutoSave();
                }
                McpServerGroups.Add(grp);
            }
        }

        if (existing != null)
        {
            Id = existing.Id;
            Name = existing.Name;
            Description = existing.Description;
            Category = existing.Category;
            SystemPrompt = existing.SystemPrompt;
            Memory = existing.Memory;
            MaxHistory = existing.MaxHistory;
            RequireToolApproval = existing.RequireToolApproval;
            IsEditing = true;
        }
        else
        {
            Id = Guid.NewGuid();
            Name = string.Empty;
            Description = string.Empty;
            Category = _nodeType == NoteNodeType.CodeRule ? "代码规则" : "规则";
            SystemPrompt = NoteNodeTypeRegistry.Get(_nodeType).DefaultContent;
            Memory = AgentMemoryMode.Off; // 规则是确定性大脑，无会话内记忆必要
            MaxHistory = 20;
            IsEditing = false;
        }

        SelectedMemoryModeOption = MemoryModeOptions.FirstOrDefault(o => o.Value == Memory);
    }

    public Guid Id { get; }
    public bool IsEditing { get; }

    public string WindowTitle =>
        _nodeType == NoteNodeType.CodeRule
            ? (IsEditing ? "编辑代码规则" : "创建代码规则")
            : (IsEditing ? "编辑规则" : "创建规则");

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "规则";
    /// <summary>规则程序 YAML（steps/match/action/noMatchReply/maxSteps）。</summary>
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private AgentMemoryMode _memory = AgentMemoryMode.Off;
    [ObservableProperty] private int _maxHistory = 20;
    [ObservableProperty] private bool _requireToolApproval;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _autoSaveStatus;

    /// <summary>扁平工具列表（所有工具，含选中状态）。</summary>
    public ObservableCollection<ToolOption> Tools { get; } = new();

    /// <summary>按类别分组的工具列表（UI 分组展示用）。</summary>
    public ObservableCollection<ToolCategoryGroup> ToolCategories { get; } = new();

    /// <summary>MCP server 工具分组（编辑器 MCP 区展示）。</summary>
    public ObservableCollection<McpServerToolGroup> McpServerGroups { get; } = new();

    /// <summary>是否有 MCP server（控制 MCP 区显隐）。</summary>
    public bool HasMcpServers => McpServerGroups.Count > 0;

    /// <summary>选中工具数量。</summary>
    public int SelectedToolCount => Tools.Count(t => t.IsSelected);

    /// <summary>记忆模式可选列表（复用 ExpertEditViewModel 的同款选项）。</summary>
    public static MemoryModeOption[] MemoryModeOptions { get; } =
    {
        new() { Value = AgentMemoryMode.Off, Display = "无记忆 · 每次独立运行" },
        new() { Value = AgentMemoryMode.SlidingWindow, Display = "滑动窗口 · 仅最近 N 条" },
        new() { Value = AgentMemoryMode.Full, Display = "完整记忆 · 保留全部历史" },
    };

    public MemoryModeOption? SelectedMemoryModeOption
    {
        get => MemoryModeOptions.FirstOrDefault(o => o.Value == Memory);
        set
        {
            if (value != null && value.Value != Memory)
            {
                Memory = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsWindowVisible => Memory == AgentMemoryMode.SlidingWindow;

    partial void OnMemoryChanged(AgentMemoryMode value)
    {
        OnPropertyChanged(nameof(IsWindowVisible));
        OnPropertyChanged(nameof(SelectedMemoryModeOption));
        ScheduleAutoSave();
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<ExpertItem>? SaveRequested;
    public event Action? CancelRequested;
    public event Action<ExpertItem>? AutoSaveRequested;

    // —— 自动保存（防抖 500ms）——
    private CancellationTokenSource? _autoSaveCts;
    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(CanSave)); ScheduleAutoSave(); }
    partial void OnDescriptionChanged(string value) => ScheduleAutoSave();
    partial void OnCategoryChanged(string value) => ScheduleAutoSave();
    partial void OnSystemPromptChanged(string value) => ScheduleAutoSave();
    partial void OnMaxHistoryChanged(int value) => ScheduleAutoSave();
    partial void OnRequireToolApprovalChanged(bool value) => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        if (!IsEditing || string.IsNullOrWhiteSpace(Name)) return;
        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var ct = _autoSaveCts.Token;
        _ = Task.Delay(500, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested) return;
            DoAutoSave();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void DoAutoSave()
    {
        var item = Build();
        if (item is null) return;
        AutoSaveRequested?.Invoke(item);
        AutoSaveStatus = "✓ 已自动保存";
        _ = Task.Delay(1500).ContinueWith(_ => AutoSaveStatus = null, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>构建规则卡片模型（Engine=Rule, NodeType=Rule）。</summary>
    public ExpertItem? Build()
    {
        if (string.IsNullOrWhiteSpace(Name)) return null;
        var toolList = Tools.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        return new ExpertItem
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description.Trim(),
            Category = string.IsNullOrWhiteSpace(Category)
                ? (_nodeType == NoteNodeType.CodeRule ? "代码规则" : "规则")
                : Category.Trim(),
            SystemPrompt = SystemPrompt,
            Engine = "Rule",
            NodeType = _nodeType,
            Tools = toolList,
            ToolApprovals = Tools.Where(t => t.IsSelected && t.ApprovalLevel != t.DeclaredLevel)
                .ToDictionary(t => t.Name, t => t.ApprovalLevel),
            Memory = Memory,
            MaxHistory = MaxHistory,
            RequireToolApproval = RequireToolApproval,
        };
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            Error = "名称不能为空";
            return;
        }
        var item = Build();
        if (item is not null)
            SaveRequested?.Invoke(item);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }
}
