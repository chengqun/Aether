using System.Collections.ObjectModel;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>参与者勾选项。</summary>
public partial class ParticipantOption : ObservableObject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>转交边编辑行。</summary>
public partial class HandoffRow : ObservableObject
{
    public List<WorkflowAgentOption> Agents { get; set; } = new();
    [ObservableProperty] private Guid _fromId;
    [ObservableProperty] private Guid _toId;
    [ObservableProperty] private string? _reason;

    public WorkflowAgentOption? SelectedFrom
    {
        get => Agents.FirstOrDefault(a => a.Id == FromId);
        set { if (value is not null) { FromId = value.Id; OnPropertyChanged(); } }
    }

    public WorkflowAgentOption? SelectedTo
    {
        get => Agents.FirstOrDefault(a => a.Id == ToId);
        set { if (value is not null) { ToId = value.Id; OnPropertyChanged(); } }
    }
}

/// <summary>工作流编辑 ViewModel。</summary>
public sealed partial class WorkflowEditViewModel : ObservableObject
{
    public WorkflowEditViewModel(WorkflowItem? existing, IReadOnlyList<WorkflowAgentOption> agents)
    {
        Agents = agents ?? Array.Empty<WorkflowAgentOption>();
        var selectedSet = new HashSet<Guid>(existing?.Participants ?? Enumerable.Empty<Guid>());

        foreach (var a in Agents)
        {
            Participants.Add(new ParticipantOption { Id = a.Id, Name = a.Name, IsSelected = selectedSet.Contains(a.Id) });
        }

        if (existing != null)
        {
            Id = existing.Id;
            Name = existing.Name;
            Description = existing.Description;
            Category = existing.Category;
            EntryAgentId = existing.EntryAgentId;
            HandoffInstructions = existing.HandoffInstructions;
            foreach (var h in existing.Handoffs)
            {
                Handoffs.Add(new HandoffRow { Agents = Agents.ToList(), FromId = h.FromAgentId, ToId = h.ToAgentId, Reason = h.Reason });
            }
            IsEditing = true;
        }
        else
        {
            Id = Guid.NewGuid();
            Category = "工作流";
            IsEditing = false;
        }

        SelectedEntry = Agents.FirstOrDefault(a => a.Id == EntryAgentId)
            ?? Agents.FirstOrDefault();
    }

    public Guid Id { get; }
    public bool IsEditing { get; }
    public string WindowTitle => IsEditing ? "编辑工作流" : "创建工作流";
    public IReadOnlyList<WorkflowAgentOption> Agents { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "工作流";
    [ObservableProperty] private Guid _entryAgentId;
    [ObservableProperty] private string? _handoffInstructions;
    [ObservableProperty] private string? _error;

    public ObservableCollection<ParticipantOption> Participants { get; } = new();
    public ObservableCollection<HandoffRow> Handoffs { get; } = new();

    public WorkflowAgentOption? SelectedEntry
    {
        get => Agents.FirstOrDefault(a => a.Id == EntryAgentId);
        set
        {
            if (value is not null && value.Id != EntryAgentId)
            {
                EntryAgentId = value.Id;
                // 入口必须勾选为参与者
                var p = Participants.FirstOrDefault(x => x.Id == value.Id);
                if (p is not null) p.IsSelected = true;
                OnPropertyChanged();
            }
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && EntryAgentId != Guid.Empty;

    [RelayCommand] private void AddHandoff() => Handoffs.Add(new HandoffRow { Agents = Agents.ToList() });
    [RelayCommand] private void RemoveHandoff(HandoffRow row) { if (row is not null) Handoffs.Remove(row); }

    /// <summary>从当前编辑数据构建图模型（供画布渲染）。</summary>
    public WorkflowGraphModel BuildGraphModel()
        => WorkflowGraphBuilder.Build(EntryAgentId, Participants, Handoffs);

    /// <summary>从当前编辑数据生成 Mermaid flowchart 字符串。</summary>
    public string BuildMermaid() => WorkflowGraphBuilder.ToMermaid(BuildGraphModel());

    public event Action<WorkflowItem>? SaveRequested;
    public event Action? CancelRequested;

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) { Error = "名称与入口 agent 不能为空"; return; }
        var item = new WorkflowItem
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description?.Trim() ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(Category) ? "工作流" : Category.Trim(),
            EntryAgentId = EntryAgentId,
            Participants = Participants.Where(p => p.IsSelected).Select(p => p.Id).ToList(),
            Handoffs = Handoffs.Where(r => r.FromId != Guid.Empty && r.ToId != Guid.Empty)
                .Select(r => new WorkflowHandoffField { FromAgentId = r.FromId, ToAgentId = r.ToId, Reason = r.Reason }).ToList(),
            HandoffInstructions = HandoffInstructions,
        };
        if (!item.Participants.Contains(item.EntryAgentId))
            item.Participants.Add(item.EntryAgentId);
        SaveRequested?.Invoke(item);
    }

    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();
}
