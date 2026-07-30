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

    private INoteService NoteService => _noteService ??= _serviceProvider.GetRequiredService<INoteService>();
    private INoteService? _noteService;

    public ObservableCollection<NoteNodeVM> Roots { get; } = new();

    [ObservableProperty]
    private NoteNodeVM? _selectedNode;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isPreviewMode;

    public event Action? NotesChanged;

    public bool IsAgentNode => SelectedNode?.NodeType == NoteNodeType.Agent;

    partial void OnSelectedNodeChanged(NoteNodeVM? value)
    {
        OnPropertyChanged(nameof(IsAgentNode));
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
                foreach (var r in vms.Where(v => !v.ParentId.HasValue).OrderBy(r => r.Title))
                    Roots.Add(r);

                SelectedNode = Roots.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    public NoteNodeEditRow BuildNewNodeRow() => new();

    public NoteNodeEditRow? BuildEditNodeRow()
    {
        if (SelectedNode == null) return null;
        var row = new NoteNodeEditRow
        {
            Name = SelectedNode.Title,
            IsTypeEnabled = false,
            Syntax = SelectedNode.Syntax,
            Tags = SelectedNode.Tags,
            IsReadOnly = false,
        };
        row.SelectedTypeOption = row.TypeOptions.FirstOrDefault(o => o.Value == SelectedNode.NodeType);
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

            var dto = await NoteService.CreateAsync(new CreateNoteRequest
            {
                Title = title,
                Content = type switch
                {
                    NoteNodeType.Agent => "# Agent 模板\n\n系统提示词...",
                    NoteNodeType.Workflow => "# Workflow 模板\n\n",
                    _ => "# 新笔记\n\n开始写作...",
                },
                ParentId = parentId?.ToString(),
                Syntax = row.Syntax,
                Tags = row.Tags,
                NodeType = type,
                Icon = row.IconKey,
            });

            await LoadAsync();
            StatusMessage = $"已创建「{title}」";
            NotesChanged?.Invoke();

            if (Guid.TryParse(dto.Id, out var newId))
            {
                var created = FindNode(Roots, newId);
                if (created?.Parent != null) created.Parent.IsExpanded = true;
                if (created != null) SelectedNode = created;
            }
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
            SelectedNode.Syntax = row.Syntax;
            SelectedNode.Tags = row.Tags;
            SelectedNode.IconKey = row.IconKey;

            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = SelectedNode.Id.ToString(),
                Title = name,
                Content = SelectedNode.Content,
                Syntax = row.Syntax,
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

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNode == null) return;
        try
        {
            var title = SelectedNode.Title;
            await NoteService.DeleteAsync(SelectedNode.Id.ToString());
            await LoadAsync();
            StatusMessage = $"已删除：{title}";
            NotesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePreview()
    {
        IsPreviewMode = !IsPreviewMode;
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