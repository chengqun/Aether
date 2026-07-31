using System.Collections.ObjectModel;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>知识对象属性行（UI 可编辑）。</summary>
public partial class KnowledgePropertyRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
}

/// <summary>知识对象关系行（UI 可编辑）。</summary>
public partial class KnowledgeRelationRow : ObservableObject
{
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
}

/// <summary>知识对象行为行（UI 可编辑）。</summary>
public partial class KnowledgeBehaviorRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _expression = string.Empty;
}

/// <summary>知识对象历史条目行（UI 可编辑字符串）。</summary>
public partial class KnowledgeHistoryRow : ObservableObject
{
    [ObservableProperty] private string _value = string.Empty;
}

/// <summary>
/// 知识对象结构化编辑 VM（镜像 ExpertEditViewModel 形态）。
/// 从 <see cref="KnowledgeObjectDefinition"/> 加载，编辑后 <see cref="Build"/> 回 Domain 模型并触发保存。
/// </summary>
public sealed partial class KnowledgeEditViewModel : ObservableObject
{
    public Guid Id { get; }

    public KnowledgeEditViewModel(Guid id, KnowledgeObjectDefinition def)
    {
        Id = id;
        Name = def.Name;
        DisplayName = def.DisplayName;
        SelectedKind = def.Kind;
        Meaning = def.Meaning;
        DataBindingSource = def.DataBinding?.Source ?? string.Empty;
        DataBindingTable = def.DataBinding?.Table ?? string.Empty;

        foreach (var p in def.Properties) Properties.Add(new KnowledgePropertyRow { Name = p.Name, Type = p.Type, Description = p.Description });
        foreach (var r in def.Relations) Relations.Add(new KnowledgeRelationRow { Target = r.Target, Type = r.Type, Description = r.Description });
        foreach (var b in def.Behavior) Behavior.Add(new KnowledgeBehaviorRow { Name = b.Name, Type = b.Type, Expression = b.Expression });
        foreach (var h in def.History) History.Add(new KnowledgeHistoryRow { Value = h });
    }

    /// <summary>Kind 下拉可选值（文档 §8）。</summary>
    public static KnowledgeObjectKind[] KindOptions { get; } =
        new[] { KnowledgeObjectKind.Entity, KnowledgeObjectKind.Metric, KnowledgeObjectKind.Rule,
                KnowledgeObjectKind.Process, KnowledgeObjectKind.Action, KnowledgeObjectKind.Concept };

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private KnowledgeObjectKind _selectedKind = KnowledgeObjectKind.Entity;
    [ObservableProperty] private string _meaning = string.Empty;
    [ObservableProperty] private string _dataBindingSource = string.Empty;
    [ObservableProperty] private string _dataBindingTable = string.Empty;

    public ObservableCollection<KnowledgePropertyRow> Properties { get; } = new();
    public ObservableCollection<KnowledgeRelationRow> Relations { get; } = new();
    public ObservableCollection<KnowledgeBehaviorRow> Behavior { get; } = new();
    public ObservableCollection<KnowledgeHistoryRow> History { get; } = new();

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    [ObservableProperty] private string? _error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<KnowledgeObjectDefinition>? SaveRequested;
    public event Action? CancelRequested;

    /// <summary>把可编辑字段投影回 Domain 模型。</summary>
    public KnowledgeObjectDefinition Build()
    {
        KnowledgeDataBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(DataBindingSource) || !string.IsNullOrWhiteSpace(DataBindingTable))
            binding = new KnowledgeDataBinding { Source = DataBindingSource ?? string.Empty, Table = DataBindingTable ?? string.Empty };

        return new KnowledgeObjectDefinition
        {
            Name = Name.Trim(),
            DisplayName = DisplayName?.Trim() ?? string.Empty,
            Kind = SelectedKind,
            Meaning = Meaning ?? string.Empty,
            Properties = Properties.Select(p => new KnowledgeProperty { Name = p.Name, Type = p.Type, Description = p.Description }).ToList(),
            Relations = Relations.Select(r => new KnowledgeRelation { Target = r.Target, Type = r.Type, Description = r.Description }).ToList(),
            DataBinding = binding,
            Behavior = Behavior.Select(b => new KnowledgeBehavior { Name = b.Name, Type = b.Type, Expression = b.Expression }).ToList(),
            History = History.Select(h => h.Value).ToList(),
        };
    }

    [RelayCommand] private void Save()
    {
        if (!CanSave) { Error = "名称不能为空"; return; }
        Error = null;
        SaveRequested?.Invoke(Build());
    }

    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();

    // —— 列表增删 ——
    [RelayCommand] private void AddProperty() => Properties.Add(new KnowledgePropertyRow());
    [RelayCommand] private void RemoveProperty(KnowledgePropertyRow row) => Properties.Remove(row);
    [RelayCommand] private void AddRelation() => Relations.Add(new KnowledgeRelationRow());
    [RelayCommand] private void RemoveRelation(KnowledgeRelationRow row) => Relations.Remove(row);
    [RelayCommand] private void AddBehavior() => Behavior.Add(new KnowledgeBehaviorRow());
    [RelayCommand] private void RemoveBehavior(KnowledgeBehaviorRow row) => Behavior.Remove(row);
    [RelayCommand] private void AddHistory() => History.Add(new KnowledgeHistoryRow());
    [RelayCommand] private void RemoveHistory(KnowledgeHistoryRow row) => History.Remove(row);
}
