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
/// 工作流新建 VM（AI 设置 → 新建 工作流）。字段 = 名称/描述/分类 + 参与者勾选（从 <see cref="ICustomAgentCatalog"/>
/// 拉 agent 列表）+ 入口 agent。经 <see cref="ICustomWorkflowCatalog.SaveAsync"/> 写 <c>labs/workflows/*.workflow.json</c>。
/// handoffs（转交边）暂不在手机端新建表单里编辑（复杂图结构，桌面端做）——新建只设参与者 + 入口。
/// </summary>
public partial class WorkflowCreateViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog _agentCatalog;
    private readonly ICustomWorkflowCatalog _workflowCatalog;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "工作流";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>参与者勾选项（agent 列表投影）。</summary>
    public ObservableCollection<WorkflowParticipantOption> Participants { get; } = new();

    /// <summary>入口 agent 的选中索引（对应 Participants）。</summary>
    [ObservableProperty] private int _entryIndex = -1;

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public WorkflowCreateViewModel(ICustomAgentCatalog agentCatalog, ICustomWorkflowCatalog workflowCatalog)
    {
        _agentCatalog = agentCatalog ?? throw new ArgumentNullException(nameof(agentCatalog));
        _workflowCatalog = workflowCatalog ?? throw new ArgumentNullException(nameof(workflowCatalog));
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载 agent 列表失败: {ex.Message}";
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
                Id = Guid.NewGuid(),
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
