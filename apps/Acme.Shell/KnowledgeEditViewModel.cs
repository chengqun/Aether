using System.Collections.ObjectModel;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>知识对象 link 行（UI 可编辑：role + target 标题）。</summary>
public partial class KnowledgeLinkRow : ObservableObject
{
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
}

/// <summary>
/// 知识对象结构化编辑 VM（最简模型：Name + Kind + Links + Body）。
/// 从 <see cref="KnowledgeObjectDefinition"/> 加载，编辑后 <see cref="Build"/> 回 Domain 模型并触发保存。
/// </summary>
public sealed partial class KnowledgeEditViewModel : ObservableObject
{
    public Guid Id { get; }

    public KnowledgeEditViewModel(Guid id, string name, KnowledgeObjectDefinition def)
    {
        Id = id;
        Name = name;
        Kind = def.Kind;
        Body = def.Body;
        foreach (var l in def.Links) Links.Add(new KnowledgeLinkRow { Role = l.Role, Target = l.Target });
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _body = string.Empty;

    public ObservableCollection<KnowledgeLinkRow> Links { get; } = new();

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    [ObservableProperty] private string? _error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<KnowledgeObjectDefinition>? SaveRequested;
    public event Action? CancelRequested;

    /// <summary>把可编辑字段投影回 Domain 模型（name 不在 def 内，由调用方用于笔记标题）。</summary>
    public KnowledgeObjectDefinition Build() => new()
    {
        Kind = Kind ?? string.Empty,
        Body = Body ?? string.Empty,
        Links = Links.Select(l => new KnowledgeLink { Role = l.Role, Target = l.Target }).ToList(),
    };

    [RelayCommand] private void Save()
    {
        if (!CanSave) { Error = "名称不能为空"; return; }
        Error = null;
        SaveRequested?.Invoke(Build());
    }

    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();

    [RelayCommand] private void AddLink() => Links.Add(new KnowledgeLinkRow());
    [RelayCommand] private void RemoveLink(KnowledgeLinkRow row) => Links.Remove(row);
}
