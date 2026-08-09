using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

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

        // 订阅集合与行变更 → debounce 自动保存
        Participants.CollectionChanged += OnParticipantsChanged;
        Handoffs.CollectionChanged += OnHandoffsChanged;
        foreach (var p in Participants) p.PropertyChanged += OnParticipantChanged;
        foreach (var h in Handoffs) h.PropertyChanged += OnHandoffRowChanged;
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
    [ObservableProperty] private string? _autoSaveStatus;

    private CancellationTokenSource? _autoSaveCts;

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

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(CanSave)); ScheduleAutoSave(); }
    partial void OnDescriptionChanged(string value) => ScheduleAutoSave();
    partial void OnCategoryChanged(string value) => ScheduleAutoSave();
    partial void OnEntryAgentIdChanged(Guid value) => ScheduleAutoSave();
    partial void OnHandoffInstructionsChanged(string? value) => ScheduleAutoSave();
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
    public event Action<WorkflowItem>? AutoSaveRequested;
    public event Action? CancelRequested;

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) { Error = "名称与入口 agent 不能为空"; return; }
        Error = null;
        var item = BuildWorkflowItem();
        if (item is not null) SaveRequested?.Invoke(item);
    }

    /// <summary>从当前编辑字段构建 WorkflowItem（Save 与自动保存共用）。</summary>
    private WorkflowItem? BuildWorkflowItem()
    {
        if (!CanSave) return null;
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
        return item;
    }

    /// <summary>字段/集合变更后延迟自动保存（500ms debounce，仅编辑现有时；新建需先手动保存一次）。</summary>
    private void ScheduleAutoSave()
    {
        if (!IsEditing || !CanSave) return;
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
        var item = BuildWorkflowItem();
        if (item is null) return;
        AutoSaveRequested?.Invoke(item);
        AutoSaveStatus = "✓ 已自动保存";
        _ = Task.Delay(1500).ContinueWith(_ => AutoSaveStatus = null, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnParticipantsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ParticipantOption p in e.NewItems) p.PropertyChanged += OnParticipantChanged;
        if (e.OldItems is not null) foreach (ParticipantOption p in e.OldItems) p.PropertyChanged -= OnParticipantChanged;
        ScheduleAutoSave();
    }

    private void OnHandoffsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (HandoffRow r in e.NewItems) r.PropertyChanged += OnHandoffRowChanged;
        if (e.OldItems is not null) foreach (HandoffRow r in e.OldItems) r.PropertyChanged -= OnHandoffRowChanged;
        ScheduleAutoSave();
    }

    private void OnParticipantChanged(object? sender, PropertyChangedEventArgs e) => ScheduleAutoSave();
    private void OnHandoffRowChanged(object? sender, PropertyChangedEventArgs e) => ScheduleAutoSave();

    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();
}
