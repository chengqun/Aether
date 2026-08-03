using System.Collections.ObjectModel;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>知识对象 link 行（UI 可编辑：role + target 标题；TargetId 由标题解析得出）。</summary>
public partial class KnowledgeLinkRow : ObservableObject
{
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    /// <summary>目标笔记 Id（加载时回填；Build 时按标题重新解析）。</summary>
    public Guid? TargetId { get; set; }
}

/// <summary>
/// 知识对象结构化编辑 VM（最简模型：Name + Kind + Links + Body）。
/// 从 <see cref="KnowledgeObjectDefinition"/> 加载，编辑后 <see cref="Build"/> 回 Domain 模型并触发保存。
/// link 的 target 标题经 <paramref name="resolveTarget"/> 解析为 TargetId（id 权威，标题用于显示）。
/// </summary>
public sealed partial class KnowledgeEditViewModel : ObservableObject
{
    private readonly Func<string?, (Guid Id, string Title)?> _resolveTarget;
    private readonly Func<Guid, string?> _resolveTitleById;

    public Guid Id { get; }

    public KnowledgeEditViewModel(Guid id, string name, KnowledgeObjectDefinition def,
        Func<string?, (Guid Id, string Title)?> resolveTarget,
        Func<Guid, string?> resolveTitleById)
    {
        Id = id;
        Name = name;
        Kind = def.Kind;
        Body = def.Body;
        _resolveTarget = resolveTarget;
        _resolveTitleById = resolveTitleById;
        foreach (var l in def.Links)
        {
            // 有 TargetId 时按 id 反查当前标题（目标被改名也能显示最新标题）；查不到回退存储标题
            var displayTitle = l.TargetId.HasValue
                ? (_resolveTitleById(l.TargetId.Value) ?? l.Target)
                : l.Target;
            Links.Add(new KnowledgeLinkRow { Role = l.Role, Target = displayTitle, TargetId = l.TargetId });
        }
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

    /// <summary>把可编辑字段投影回 Domain 模型（name 不在 def 内，由调用方用于笔记标题）。
    /// link 按当前标题重新解析 TargetId（标题改了就换 id；解析不到则 TargetId=null，读取时回退标题）。</summary>
    public KnowledgeObjectDefinition Build() => new()
    {
        Kind = Kind ?? string.Empty,
        Body = Body ?? string.Empty,
        Links = Links.Select(l =>
        {
            var resolved = _resolveTarget(l.Target);
            return new KnowledgeLink
            {
                Role = l.Role,
                // 标题解析得到→用新 id；解析不到（目标被改名/删除/未改动）→保留原 id（移动/改名都不断链）
                TargetId = resolved?.Id ?? l.TargetId,
                Target = resolved?.Title ?? l.Target,
            };
        }).ToList(),
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
