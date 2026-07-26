using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Shell;

/// <summary>笔记页面 ViewModel：左侧树状笔记列表 + 右侧 Markdown 编辑。</summary>
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

    /// <summary>笔记数据变化事件（专家页等订阅刷新）。</summary>
    public event Action? NotesChanged;

    public NotesViewModel(IServiceProvider serviceProvider, IUiDispatcher dispatcher)
    {
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
    }

    /// <summary>加载全部笔记并构建树。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await NoteService.GetAllAsync();
            var vms = all.Select(NoteNodeVM.FromDto).ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                // 先按 ParentId 分组
                var byParent = new Dictionary<Guid, List<NoteNodeVM>>();
                var roots = new List<NoteNodeVM>();
                var lookup = vms.ToDictionary(n => n.Id);

                foreach (var node in vms)
                {
                    if (node.ParentId.HasValue && lookup.TryGetValue(node.ParentId.Value, out var parent))
                    {
                        if (!byParent.ContainsKey(node.ParentId.Value))
                            byParent[node.ParentId.Value] = new List<NoteNodeVM>();
                        byParent[node.ParentId.Value].Add(node);
                        node.Parent = parent;
                    }
                    else
                    {
                        roots.Add(node);
                    }
                }

                // 挂子节点
                foreach (var kv in byParent)
                {
                    if (lookup.TryGetValue(kv.Key, out var parent))
                    {
                        foreach (var child in kv.Value.OrderBy(c => c.Title))
                            parent.Children.Add(child);
                    }
                }

                Roots.Clear();
                foreach (var r in roots.OrderBy(r => r.Title))
                    Roots.Add(r);

                // 默认选中第一个根节点
                SelectedNode = Roots.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>新建根节点笔记。</summary>
    [RelayCommand]
    private async Task CreateRootNoteAsync()
    {
        try
        {
            var title = "新笔记";
            var dto = await NoteService.CreateAsync(new CreateNoteRequest
            {
                Title = title,
                Content = "# 新笔记\n\n开始写作...",
            });
            await LoadAsync();
            StatusMessage = $"已创建：{title}";
            NotesChanged?.Invoke();

            // 选中新建的节点
            if (Guid.TryParse(dto.Id, out var newId))
            {
                var created = FindNode(Roots, newId);
                if (created != null) SelectedNode = created;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建失败：{ex.Message}";
        }
    }

    /// <summary>在选中节点下新建子笔记。</summary>
    [RelayCommand]
    private async Task CreateChildNoteAsync()
    {
        if (SelectedNode == null) return;
        try
        {
            var title = "新子笔记";
            var dto = await NoteService.CreateAsync(new CreateNoteRequest
            {
                ParentId = SelectedNode.Id.ToString(),
                Title = title,
                Content = "# 新子笔记\n\n",
            });
            SelectedNode.IsExpanded = true;
            await LoadAsync();
            StatusMessage = $"已创建子笔记：{title}";
            NotesChanged?.Invoke();

            // 展开父节点并选中新建的
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

    /// <summary>保存当前选中的笔记。</summary>
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

    /// <summary>删除当前选中的笔记。</summary>
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

    /// <summary>切换预览/编辑模式。</summary>
    [RelayCommand]
    private void TogglePreview()
    {
        IsPreviewMode = !IsPreviewMode;
    }

    /// <summary>在树中递归查找节点。</summary>
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
}
