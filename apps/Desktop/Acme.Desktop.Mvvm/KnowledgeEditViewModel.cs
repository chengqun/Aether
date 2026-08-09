using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>可选目标节点（任意笔记，下拉选择用）。</summary>
public sealed record TargetOption(Guid Id, string Title);

/// <summary>可选目标节点的树形结构（供 TreeView 递归绑定，按层级展开选择）。</summary>
public sealed record TargetTreeNode(Guid Id, string Title, IReadOnlyList<TargetTreeNode> Children);

/// <summary>知识对象 link 行（UI 可编辑：role + target 节点选择）。</summary>
public partial class KnowledgeLinkRow : ObservableObject
{
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    /// <summary>目标笔记 Id（加载时回填；Build 时优先取 SelectedTarget.Id）。</summary>
    public Guid? TargetId { get; set; }

    /// <summary>下拉选中的目标节点。选中即同步 Target 标题与 TargetId（不再依赖标题解析）。</summary>
    [ObservableProperty] private TargetOption? _selectedTarget;
    partial void OnSelectedTargetChanged(TargetOption? value)
    {
        // null 时保留原 Target/TargetId（目标可能已被删除，不清空已存的引用）
        if (value is null) return;
        Target = value.Title;
        TargetId = value.Id;
    }
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
    private readonly Func<string?, (string Title, KnowledgeObjectDefinition? Def)?> _resolveTargetDef;

    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private Timer? _saveTimer;
    private bool _loading = true;
    private bool _syncing;

    public Guid Id { get; }

    /// <summary>可 link 的目标节点（所有笔记节点，排除自身），供 target 下拉选择。</summary>
    public IReadOnlyList<TargetOption> AvailableTargets { get; }

    /// <summary>可 link 的目标节点树（按层级展开，供树形选择窗口）。</summary>
    public IReadOnlyList<TargetTreeNode> AvailableTargetTree { get; }

    public KnowledgeEditViewModel(Guid id, string name, KnowledgeObjectDefinition def,
        IReadOnlyList<TargetOption> availableTargets,
        IReadOnlyList<TargetTreeNode> availableTargetTree,
        Func<string?, (Guid Id, string Title)?> resolveTarget,
        Func<Guid, string?> resolveTitleById,
        Func<string?, (string Title, KnowledgeObjectDefinition? Def)?> resolveTargetDef)
    {
        Id = id;
        _saveTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
        Name = name;
        Kind = def.Kind;
        Body = def.Body;
        AvailableTargets = availableTargets;
        AvailableTargetTree = availableTargetTree;
        _resolveTarget = resolveTarget;
        _resolveTitleById = resolveTitleById;
        _resolveTargetDef = resolveTargetDef;
        foreach (var l in def.Links)
        {
            // 有 TargetId 时按 id 反查当前标题（目标被改名也能显示最新标题）；查不到回退存储标题
            var displayTitle = l.TargetId.HasValue
                ? (_resolveTitleById(l.TargetId.Value) ?? l.Target)
                : l.Target;
            var row = new KnowledgeLinkRow { Role = l.Role, Target = displayTitle, TargetId = l.TargetId };
            // 回填下拉选中项：优先按 TargetId 匹配可选列表，回退按标题（老数据 targetId 为空）
            row.SelectedTarget = AvailableTargets.FirstOrDefault(t => t.Id == l.TargetId)
                ?? AvailableTargets.FirstOrDefault(t => string.Equals(t.Title, displayTitle, StringComparison.OrdinalIgnoreCase));
            row.PropertyChanged += OnLinkRowChanged;
            Links.Add(row);
        }
        Links.CollectionChanged += OnLinksCollectionChanged;
        _loading = false;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _body = string.Empty;

    public ObservableCollection<KnowledgeLinkRow> Links { get; } = new();

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(CanSave)); ScheduleSave(); }
    partial void OnKindChanged(string value) => ScheduleSave();
    partial void OnBodyChanged(string value) { if (!_syncing) ScheduleSave(); }

    /// <summary>从下栏源码同步 body 到本 VM（渲染实时刷新）；_syncing 期间不触发自动保存（下栏已自行存库）。</summary>
    public void SyncBody(string body)
    {
        _syncing = true;
        try { Body = body; }
        finally { _syncing = false; }
    }

    [ObservableProperty] private string? _error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<KnowledgeObjectDefinition>? SaveRequested;
    public event Action? CancelRequested;

    /// <summary>把可编辑字段投影回 Domain 模型（name 不在 def 内，由调用方用于笔记标题）。
    /// link 优先取 SelectedTarget（下拉选的真实节点）；为 null 时回退标题解析（手打/老数据）。</summary>
    public KnowledgeObjectDefinition Build() => new()
    {
        Kind = Kind ?? string.Empty,
        Body = Body ?? string.Empty,
        Links = Links.Select(l =>
        {
            // 优先用下拉选中的真实节点（id 权威，不会因标题改名断链）
            if (l.SelectedTarget is { } opt)
                return new KnowledgeLink { Role = l.Role, TargetId = opt.Id, Target = opt.Title };
            // 回退：标题解析（SelectedTarget 为 null 但 Target 有值时）
            var resolved = _resolveTarget(l.Target);
            return new KnowledgeLink
            {
                Role = l.Role,
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

    /// <summary>字段/links 变更后延迟自动保存（1500ms debounce，与普通笔记一致）。</summary>
    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer?.Change(1500, Timeout.Infinite);
    }

    /// <summary>debounce 到期：回 UI 线程触发 SaveRequested（订阅方校验当前节点 id 后写库）。</summary>
    private void FlushSave()
    {
        if (!CanSave) return;
        _syncContext?.Post(_ => SaveRequested?.Invoke(Build()), null);
    }

    private void OnLinksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (KnowledgeLinkRow r in e.NewItems) r.PropertyChanged += OnLinkRowChanged;
        if (e.OldItems is not null) foreach (KnowledgeLinkRow r in e.OldItems) r.PropertyChanged -= OnLinkRowChanged;
        ScheduleSave();
    }

    private void OnLinkRowChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    /// <summary>从当前知识对象出发构建出边闭包子图（焦点高亮，不串业务）。</summary>
    public WorkflowGraphModel BuildGraphModel()
        => KnowledgeGraphBuilder.BuildClosure(Name, Build(), _resolveTargetDef);

    /// <summary>从当前知识对象出发生成 Mermaid flowchart 字符串。</summary>
    public string BuildMermaid() => WorkflowGraphBuilder.ToMermaid(BuildGraphModel());
}
