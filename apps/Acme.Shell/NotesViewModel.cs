using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Shell;

public partial class NotesViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _dispatcher;

    private INoteService NoteService
    {
        get
        {
            if (_noteService is null)
            {
                _noteService = _serviceProvider.GetRequiredService<INoteService>();
                _noteService.NotesChanged += OnNotesChangedExternal;
            }
            return _noteService;
        }
    }
    private INoteService? _noteService;
    private bool _suppressExternalRefresh;

    public ObservableCollection<NoteNodeVM> Roots { get; } = new();

    [ObservableProperty]
    private NoteNodeVM? _selectedNode;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public event Action? NotesChanged;

    public bool IsAgentNode => SelectedNode?.NodeType == NoteNodeType.Agent;

    /// <summary>文本节点 → AvaloniaEdit 编辑（按语法高亮，含 markdown 高亮）。</summary>
    /// <summary>所有节点都用 AvaloniaEdit 编辑（Agent=YAML纯文本、Workflow=JSON、文本=按语法高亮）。</summary>
    public bool IsCodeEdit => SelectedNode is not null;

    partial void OnSelectedNodeChanged(NoteNodeVM? value)
    {
        OnPropertyChanged(nameof(IsAgentNode));
        OnPropertyChanged(nameof(IsCodeEdit));
    }

    public NotesViewModel(IServiceProvider serviceProvider, IUiDispatcher dispatcher)
    {
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync()
    {
        try
        {
            var all = await NoteService.GetAllAsync();
            var vms = all.Select(NoteNodeVM.FromDto).ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                var lookup = vms.ToDictionary(n => n.Id);

                foreach (var node in vms)
                {
                    if (node.ParentId.HasValue && lookup.TryGetValue(node.ParentId.Value, out var parent))
                    {
                        parent.Children.Add(node);
                        node.Parent = parent;
                    }
                }

                Roots.Clear();
                foreach (var r in vms.Where(v => !v.ParentId.HasValue)
                                     .OrderBy(r => r.SortOrder)
                                     .ThenBy(r => r.Title))
                    Roots.Add(r);

                SelectedNode = Roots.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>上次新建节点用的语法（同会话内记忆，免去每次重选）。</summary>
    private string? _lastNewSyntax;

    public NoteNodeEditRow BuildNewNodeRow()
    {
        var row = new NoteNodeEditRow();
        if (_lastNewSyntax is not null)
            row.SelectedSyntaxOption = row.SyntaxOptions.FirstOrDefault(o => o.Value == _lastNewSyntax);
        return row;
    }

    /// <summary>外部（如 AI 工具）修改笔记后触发：刷新树，保持展开/选中状态。</summary>
    private void OnNotesChangedExternal()
    {
        if (_suppressExternalRefresh) return;
        _dispatcher.Post(async () => await RefreshAsync());
    }

    /// <summary>重新加载树，保持展开状态和选中节点。</summary>
    public async Task RefreshAsync()
    {
        var selectedId = SelectedNode?.Id;
        var expanded = new HashSet<Guid>();
        CollectExpanded(Roots, expanded);

        await LoadAsync();

        RestoreExpanded(Roots, expanded);
        if (selectedId.HasValue)
        {
            var node = FindNode(Roots, selectedId.Value);
            if (node != null) SelectedNode = node;
        }
    }

    private static void CollectExpanded(IEnumerable<NoteNodeVM> nodes, HashSet<Guid> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsExpanded) ids.Add(n.Id);
            CollectExpanded(n.Children, ids);
        }
    }

    private static void RestoreExpanded(IEnumerable<NoteNodeVM> nodes, HashSet<Guid> ids)
    {
        foreach (var n in nodes)
        {
            if (ids.Contains(n.Id)) n.IsExpanded = true;
            RestoreExpanded(n.Children, ids);
        }
    }

    public NoteNodeEditRow? BuildEditNodeRow()
    {
        if (SelectedNode == null) return null;
        var row = new NoteNodeEditRow
        {
            Name = SelectedNode.Title,
            IsTypeEnabled = false,
            Tags = SelectedNode.Tags,
            IsReadOnly = false,
        };
        row.SelectedTypeOption = row.TypeOptions.FirstOrDefault(o => o.Value == SelectedNode.NodeType);
        row.SelectedSyntaxOption = row.SyntaxOptions.FirstOrDefault(o => o.Value == SelectedNode.Syntax) ?? row.SyntaxOptions[0];
        row.SelectIconByKey(SelectedNode.IconKey);
        return row;
    }

    public async Task ApplyNewNodeAsync(NoteNodeEditRow row, bool asChild)
    {
        try
        {
            var type = row.SelectedType;
            var title = string.IsNullOrWhiteSpace(row.Name)
                ? (type == NoteNodeType.Agent ? "新 Agent"
                   : type == NoteNodeType.Workflow ? "新专家团" : "新笔记")
                : row.Name;

            Guid? parentId = asChild
                ? SelectedNode?.Id
                : SelectedNode?.ParentId;

            // 新节点默认排到同级末尾（SortOrder = 同级最大 + 1）
            IList<NoteNodeVM> siblings = asChild
                ? (SelectedNode?.Children ?? Roots)
                : (SelectedNode?.Parent?.Children ?? Roots);
            var nextOrder = siblings.Count == 0 ? 0 : siblings.Max(s => s.SortOrder) + 1;

            var dto = await NoteService.CreateAsync(new CreateNoteRequest
            {
                Title = title,
                Content = type switch
                {
                    NoteNodeType.Agent => "# Agent 模板\n\n系统提示词...",
                    NoteNodeType.Workflow => "# Workflow 模板\n\n",
                    _ => "",
                },
                ParentId = parentId?.ToString(),
                Syntax = row.SyntaxValue,
                Tags = row.Tags,
                NodeType = type,
                Icon = row.IconKey,
                SortOrder = nextOrder,
            });

            // 记住本次新建用的语法，下次新建默认沿用
            _lastNewSyntax = row.SyntaxValue;

            // 直接插入现有树，保持其他节点展开/折叠状态不变（不全量重建）
            var created = NoteNodeVM.FromDto(dto);
            NoteNodeVM? parent = parentId.HasValue ? FindNode(Roots, parentId.Value) : null;
            if (parent != null)
            {
                parent.Children.Add(created);
                created.Parent = parent;
                parent.IsExpanded = true; // 仅展开新节点的父级，让新节点可见
            }
            else
            {
                Roots.Add(created);
            }
            SelectedNode = created;

            StatusMessage = $"已创建「{title}」";
            NotesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建失败：{ex.Message}";
        }
    }

    public async Task ApplyEditNodeAsync(NoteNodeEditRow row)
    {
        if (SelectedNode == null) return;
        try
        {
            var name = string.IsNullOrWhiteSpace(row.Name) ? SelectedNode.Title : row.Name;

            SelectedNode.Title = name;
            SelectedNode.Syntax = row.SyntaxValue;
            SelectedNode.Tags = row.Tags;
            SelectedNode.IconKey = row.IconKey;

            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = SelectedNode.Id.ToString(),
                Title = name,
                Content = SelectedNode.Content,
                Syntax = row.SyntaxValue,
                Tags = row.Tags,
                Icon = row.IconKey ?? "",
            });

            StatusMessage = $"已保存「{name}」";
            NotesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    public async Task MoveNodeAsync(Guid sourceId, Guid? newParentId)
    {
        try
        {
            var source = FindNode(Roots, sourceId);
            if (source == null) { StatusMessage = "找不到待移动节点"; return; }
            if (newParentId == sourceId) return;

            var newParent = newParentId.HasValue ? FindNode(Roots, newParentId.Value) : null;
            if (newParentId.HasValue && newParent == null) { StatusMessage = "找不到目标父节点"; return; }

            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = sourceId.ToString(),
                Title = source.Title,
                Content = source.Content,
                ParentId = newParentId?.ToString(),
            });

            await _dispatcher.InvokeAsync(() =>
            {
                RemoveFromTree(Roots, source);
                source.ParentId = newParentId;
                if (newParent != null)
                {
                    newParent.Children.Add(source);
                    newParent.IsExpanded = true;
                }
                else
                {
                    Roots.Add(source);
                }
                SelectedNode = source;
            });
            StatusMessage = $"已移动「{source.Title}」";
        }
        catch (Exception ex)
        {
            StatusMessage = $"移动失败：{ex.Message}";
        }
    }

    /// <summary>同级上移。</summary>
    [RelayCommand]
    private async Task MoveNodeUpAsync()
    {
        if (SelectedNode is not { } node) return;
        var siblings = GetSiblings(node);
        var idx = siblings.IndexOf(node);
        if (idx <= 0) return;
        siblings[idx] = siblings[idx - 1];
        siblings[idx - 1] = node;
        await PersistOrderAsync(siblings);
        SelectedNode = node;
    }

    /// <summary>同级下移。</summary>
    [RelayCommand]
    private async Task MoveNodeDownAsync()
    {
        if (SelectedNode is not { } node) return;
        var siblings = GetSiblings(node);
        var idx = siblings.IndexOf(node);
        if (idx < 0 || idx >= siblings.Count - 1) return;
        siblings[idx] = siblings[idx + 1];
        siblings[idx + 1] = node;
        await PersistOrderAsync(siblings);
        SelectedNode = node;
    }

    private ObservableCollection<NoteNodeVM> GetSiblings(NoteNodeVM node)
        => node.Parent?.Children ?? Roots;

    /// <summary>按集合当前顺序重写同级 SortOrder 并持久化（仅写变化的）。</summary>
    private async Task PersistOrderAsync(IList<NoteNodeVM> siblings)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var n = siblings[i];
            if (n.SortOrder == i) continue;
            n.SortOrder = i;
            try
            {
                await NoteService.UpdateAsync(new UpdateNoteRequest
                {
                    Id = n.Id.ToString(),
                    Title = n.Title,
                    Content = n.Content,
                    SortOrder = i,
                });
            }
            catch { /* 单条失败不阻断其余 */ }
        }
        StatusMessage = "顺序已保存";
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (SelectedNode == null) return;
        try
        {
            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = SelectedNode.Id.ToString(),
                Title = SelectedNode.Title,
                Content = SelectedNode.Content,
                Syntax = SelectedNode.Syntax,
                Tags = SelectedNode.Tags,
            });
            StatusMessage = "已保存";
            NotesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>删除当前选中节点（供确认对话框后调用）。</summary>
    public Task DeleteSelectedAsync() => DeleteNoteAsync();

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNode is not { } node) return;
        try
        {
            var title = node.Title;
            await NoteService.DeleteAsync(node.Id.ToString());

            await _dispatcher.InvokeAsync(() =>
            {
                // 子节点提升为根（DB 已置 parent_id=NULL），保持其展开状态
                foreach (var child in node.Children.ToList())
                {
                    child.Parent = null;
                    child.ParentId = null;
                    Roots.Add(child);
                }
                node.Children.Clear();
                // 从父级或 Roots 移除被删节点
                if (node.Parent != null) node.Parent.Children.Remove(node);
                else Roots.Remove(node);
                SelectedNode = Roots.FirstOrDefault();
            });
            StatusMessage = $"已删除：{title}";
            NotesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    private static NoteNodeVM? FindNode(IEnumerable<NoteNodeVM> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            var child = FindNode(n.Children, id);
            if (child != null) return child;
        }
        return null;
    }

    private static bool RemoveFromTree(ObservableCollection<NoteNodeVM> list, NoteNodeVM node)
    {
        if (list.Remove(node)) return true;
        foreach (var n in list)
            if (RemoveFromTree(n.Children, node)) return true;
        return false;
    }
}