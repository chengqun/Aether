using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 工作流新建/编辑 VM（AI 设置 → 工作流）。字段 = 名称/描述/分类 + 参与者勾选（从 <see cref="ICustomAgentCatalog"/>
/// 拉 agent 列表）+ 入口 agent。经 <see cref="ICustomWorkflowCatalog.SaveAsync"/> 写 <c>labs/workflows/*.workflow.json</c>。
/// handoffs（转交边）暂不在手机端表单里编辑（复杂图结构，桌面端做）——只设参与者 + 入口。
/// 编辑态经导航参数 <c>id</c>（Guid）经 <see cref="ICustomWorkflowCatalog.GetAsync"/> 回填，保存按 Id 覆盖，可删除。
/// </summary>
public partial class WorkflowCreateViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog _agentCatalog;
    private readonly ICustomWorkflowCatalog _workflowCatalog;

    /// <summary>编辑态：工作流 Id。null=新建；null 但请求过编辑且未找到=按空处理。</summary>
    private Guid? _editingId;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "工作流";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>参与者勾选项（agent 列表投影）。</summary>
    public ObservableCollection<WorkflowParticipantOption> Participants { get; } = new();

    /// <summary>入口 agent 的选中索引（对应 Participants）。</summary>
    [ObservableProperty] private int _entryIndex = -1;

    /// <summary>是否编辑态（true=编辑已有工作流，显示删除按钮；false=新建）。</summary>
    public bool IsEditing => _editingId.HasValue;

    /// <summary>页标题（编辑/新建）。</summary>
    public string ScreenTitle => IsEditing ? "编辑工作流" : "新建工作流";

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public WorkflowCreateViewModel(ICustomAgentCatalog agentCatalog, ICustomWorkflowCatalog workflowCatalog)
    {
        _agentCatalog = agentCatalog ?? throw new ArgumentNullException(nameof(agentCatalog));
        _workflowCatalog = workflowCatalog ?? throw new ArgumentNullException(nameof(workflowCatalog));
    }

    /// <summary>接收导航参数：<c>id</c>（Guid）非空 = 编辑态（经 catalog 加载已有工作流回填）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var raw) && Guid.TryParse(raw?.ToString(), out var id) && id != Guid.Empty)
        {
            _editingId = id;
            _ = LoadExistingAsync();
        }
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ScreenTitle));
    }

    public override async Task OnActivatedAsync()
    {
        if (Participants.Count > 0) return; // 已加载
        try
        {
            var agents = await _agentCatalog.ListAsync();
            Participants.Clear();
            foreach (var a in agents.Where(a => a.NodeType == Acme.Notes.Domain.NoteNodeType.Agent))
                Participants.Add(new WorkflowParticipantOption { Id = a.Id, Name = a.Name, IsSelected = false });

            // 编辑态：participant 列表就绪后回填已有勾选/入口。
            if (_editingId.HasValue) await ApplyExistingSelectionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载 agent 列表失败: {ex.Message}";
        }
    }

    private async Task LoadExistingAsync()
    {
        // 预拉已有定义（OnActivatedAsync 会在 participant 就绪后回填勾选）。
        if (!_editingId.HasValue) return;
    }

    private async Task ApplyExistingSelectionsAsync()
    {
        if (!_editingId.HasValue) return;
        try
        {
            var def = await _workflowCatalog.GetAsync(_editingId.Value);
            if (def == null) return;
            Name = def.Name;
            Description = def.Description;
            Category = def.Category;
            // 回填参与者勾选
            var pidSet = new HashSet<Guid>(def.Participants ?? Array.Empty<Guid>());
            foreach (var p in Participants) p.IsSelected = pidSet.Contains(p.Id);
            // 回填入口索引（EntryIndex 是「已勾选参与者列表内」的索引，对应 Save 的 selected 列表，非 Participants 全表）
            var selectedIds = Participants.Where(p => p.IsSelected).Select(p => p.Id).ToList();
            var entryPos = selectedIds.IndexOf(def.EntryAgentId);
            EntryIndex = entryPos >= 0 ? entryPos : 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WorkflowCreate] 加载工作流失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "请填写名称";
            return;
        }
        var selected = Participants.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "请至少勾选一个参与者";
            return;
        }

        IsLoading = true;
        try
        {
            // 入口：选中索引对应的参与者；未选则默认第一个勾选的。
            var entryId = EntryIndex >= 0 && EntryIndex < selected.Count
                ? selected[EntryIndex].Id
                : selected[0].Id;

            var def = new CustomWorkflowDefinition
            {
                Id = _editingId ?? Guid.NewGuid(),
                Name = Name.Trim(),
                Description = Description?.Trim() ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(Category) ? "工作流" : Category.Trim(),
                EntryAgentId = entryId,
                Participants = selected.Select(p => p.Id).ToList(),
                Handoffs = Array.Empty<WorkflowHandoffField>(),
            };

            await Task.Run(() => _workflowCatalog.SaveAsync(def));
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除（仅编辑态）。</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!_editingId.HasValue) return;
        try
        {
            await Task.Run(() => _workflowCatalog.DeleteAsync(_editingId.Value));
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}

/// <summary>工作流参与者勾选项。</summary>
public partial class WorkflowParticipantOption : ObservableObject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}
