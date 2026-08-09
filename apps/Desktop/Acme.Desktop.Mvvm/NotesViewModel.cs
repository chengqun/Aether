using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using Acme.Agents.AI;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    private IKnowledgeSourceParser? _knowledgeParser;

    private IKnowledgeSourceParser KnowledgeParser =>
        _knowledgeParser ??= _serviceProvider.GetRequiredService<IKnowledgeSourceParser>();

    private ICustomAgentRunner? _agentRunner;
    /// <summary>自定义 agent 运行时（agent/规则/workflow 笔记源码栏直改后失效缓存，否则召唤仍跑旧程序）。</summary>
    private ICustomAgentRunner? AgentRunner => _agentRunner ??= _serviceProvider.GetService<ICustomAgentRunner>();

    private NotesEditorOptions? _editorOptions;
    private NotesEditorOptions EditorOptions =>
        _editorOptions ??= _serviceProvider.GetService<IOptions<NotesEditorOptions>>()?.Value ?? new();
    /// <summary>粘贴图片的保存根目录（markdown 引用相对文件名，预览通过 AssetPathRoot 解析）。</summary>
    public string? ImageRoot => EditorOptions.ImageRoot;

    private bool _suppressExternalRefresh;
    private bool _dirty;
    private NoteNodeVM? _previousSelected;
    private CancellationTokenSource? _saveCts;

    /// <summary>标记当前节点有未保存改动（由编辑器 TextChanged 调用），启动延迟自动保存。</summary>
    public void MarkDirty()
    {
        _dirty = true;
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        _ = DebouncedSaveAsync(_saveCts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); }
        catch (TaskCanceledException) { return; }
        if (ct.IsCancellationRequested || SelectedNode is null) return;
        await SaveNodeInternalAsync(SelectedNode);
        _dirty = false;
    }

    /// <summary>保存单个节点到 DB（抑制自身触发的刷新事件）。</summary>
    private async Task SaveNodeInternalAsync(NoteNodeVM node)
    {
        try
        {
            _suppressExternalRefresh = true;
            try
            {
                await NoteService.UpdateAsync(new UpdateNoteRequest
                {
                    Id = node.Id.ToString(),
                    Title = node.Title,
                    Content = node.Content,
                    Syntax = node.Syntax,
                    Tags = node.Tags,
                    Icon = node.IconKey ?? "",
                });

                // 源码栏直改 agent/规则/workflow 笔记后，失效运行时缓存的 agent。
                // 否则已召唤的规则/专家仍跑旧程序（如改了规则 steps 却不生效）。
                // 用描述符 HasAgentRunner 旗标——单点声明哪些类型有 agent 运行时缓存。
                if (NoteNodeTypeRegistry.Get(node.NodeType).HasAgentRunner)
                {
                    AgentRunner?.Invalidate(node.Id);
                }
            }
            finally { _suppressExternalRefresh = false; }
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 表单自动保存（如调整 per-tool 权限、改 Knowledge 字段）：用 <see cref="_suppressExternalRefresh"/> 抑制保存触发的整树 Reload
    /// （避免明显刷新感，同 <see cref="SaveNodeInternalAsync"/> 的静默模式），落库后回读选中节点内容供源码栏同步
    /// （含 tool_approvals）。不重载树、不重建表单。返回保存结果（含错误信息）。
    /// </summary>
    public async Task<(bool Success, string? Error)> SaveFormItemSilentlyAsync(Func<Task<(bool Success, string? Error)>> saveAction, Guid nodeId)
    {
        _suppressExternalRefresh = true;
        try
        {
            var result = await saveAction();
            if (!result.Success) return result;
            if (SelectedNode is { } node && node.Id == nodeId)
            {
                var dto = await NoteService.GetAsync(nodeId.ToString());
                if (dto is not null)
                {
                    node.Content = dto.Content ?? "";
                    if (!string.IsNullOrWhiteSpace(dto.Title)) node.Title = dto.Title;
                }
            }
            return result;
        }
        finally { _suppressExternalRefresh = false; }
    }

    public ObservableCollection<NoteNodeVM> Roots { get; } = new();

    [ObservableProperty]
    private NoteNodeVM? _selectedNode;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public event Action? NotesChanged;

    /// <summary>编辑器字体大小（持久化到偏好，重启保留）。</summary>
    [ObservableProperty] private double _editorFontSize = 13;
    private bool _loadingPref;

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!_loadingPref) _ = PersistFontSizeAsync(value);
    }

    private async Task PersistFontSizeAsync(double value)
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null)
                await store.SetAsync("notes.editorFontSize", value.ToString("0.#"));
        }
        catch { /* 偏好存储失败不影响编辑 */ }
    }

    public bool IsAgentNode => SelectedNode?.NodeType == NoteNodeType.Agent;
    public bool IsWorkflowNode => SelectedNode?.NodeType == NoteNodeType.Workflow;
    public bool IsKnowledgeNode => SelectedNode?.NodeType == NoteNodeType.Knowledge;
    public bool IsRuleNode => SelectedNode?.NodeType == NoteNodeType.Rule;
    public bool IsCodeRuleNode => SelectedNode?.NodeType == NoteNodeType.CodeRule;
    // IsFormNode/HasRightPane 由描述符旗标驱动——单点声明哪些类型有右栏表单，避免散落类型表。
    public bool IsFormNode => SelectedNode is not null && NoteNodeTypeRegistry.Get(SelectedNode.NodeType).IsFormNode;
    public bool HasRightPane => IsMarkdownNode || IsFormNode;

    public bool IsCodeEdit => SelectedNode is not null;
    public bool IsMarkdownNode => SelectedNode?.IsMarkdown ?? false;

    [ObservableProperty] private object? _activeEditVm;
    [ObservableProperty] private bool _isPreviewOnly;

    public bool EditorVisible => IsCodeEdit && !(HasRightPane && IsPreviewOnly);
    public bool SplitterVisible => HasRightPane && !IsPreviewOnly;
    public bool RightPaneVisible => HasRightPane;
    // 上下布局：预览/表单在上（Row 0），源码编辑器在下（Row 2），中间为可拖分隔条。
    // 仅显示上栏时编辑器隐藏、上栏跨满；纯代码节点（无右栏）时编辑器跨满。
    // markdown 与 form 节点一致：上=预览/表单，下=源码。
    public int EditorRow => (HasRightPane && !IsPreviewOnly) ? 2 : 0;
    public int EditorRowSpan => !IsCodeEdit ? 1 : (HasRightPane && !IsPreviewOnly ? 1 : 3);
    public int RightPaneRowSpan => IsPreviewOnly ? 3 : 1;

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(IsMarkdownNode));
        OnPropertyChanged(nameof(IsWorkflowNode));
        OnPropertyChanged(nameof(IsKnowledgeNode));
        OnPropertyChanged(nameof(IsRuleNode));
        OnPropertyChanged(nameof(IsCodeRuleNode));
        OnPropertyChanged(nameof(IsFormNode));
        OnPropertyChanged(nameof(HasRightPane));
        OnPropertyChanged(nameof(EditorVisible));
        OnPropertyChanged(nameof(SplitterVisible));
        OnPropertyChanged(nameof(RightPaneVisible));
        OnPropertyChanged(nameof(EditorRow));
        OnPropertyChanged(nameof(EditorRowSpan));
        OnPropertyChanged(nameof(RightPaneRowSpan));
    }

    /// <summary>语法变化后由视图调用，刷新 markdown 预览栏可见性。</summary>
    public void RaiseIsMarkdownNode() => RaiseLayout();

    /// <summary>把知识对象节点的 YAML 内容解析为定义模型（供视图层构建编辑 VM）。</summary>
    public (KnowledgeObjectDefinition? Def, string? Error) TryParseKnowledge(string content)
    {
        if (!KnowledgeParser.TryParse(content, out var def, out var error))
            return (null, error);
        return (def, null);
    }

    private ICodeSourceParser? _codeParser;
    private ICodeSourceParser CodeParser =>
        _codeParser ??= _serviceProvider.GetRequiredService<ICodeSourceParser>();

    /// <summary>
    /// 从代码规则节点的完整内容（front-matter + C# 正文）取出 C# 正文。
    /// 供表单自动保存前回填 <c>SystemPrompt</c>——否则表单用编辑 VM 构造时的快照覆盖下栏最新 C# 改动。
    /// 解析失败（如编辑中 front-matter 不完整）回退原文本，让编译器报真实错。
    /// </summary>
    public string CodeBodyFromContent(string content)
    {
        if (CodeParser.TryParse(content, out var def, out _) && def is not null)
            return def.SystemPrompt ?? content;
        return content;
    }

    /// <summary>保存知识对象：序列化 def → 更新笔记 Content/Title → 刷新树。name 单独传（def 内无 Name）。</summary>
    public async Task<(bool Success, string? Error)> SaveKnowledgeAsync(KnowledgeObjectDefinition def, string name)
    {
        if (SelectedNode is null) return (false, "未选中节点");
        try
        {
            var source = KnowledgeParser.Serialize(def);
            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = SelectedNode.Id.ToString(),
                Title = name,
                Content = source,
                NodeType = NoteNodeType.Knowledge,
            });
            await RefreshAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    partial void OnIsPreviewOnlyChanged(bool value) => RaiseLayout();

    [RelayCommand]
    private void TogglePreviewOnly() => IsPreviewOnly = !IsPreviewOnly;

    partial void OnSelectedNodeChanged(NoteNodeVM? value)
    {
        if (_dirty && _previousSelected is not null)
        {
            _saveCts?.Cancel();
            _ = SaveNodeInternalAsync(_previousSelected);
            _dirty = false;
        }
        _previousSelected = value;
        ActiveEditVm = null; // 切换节点时清空表单 VM（视图层会按需重建）

        OnPropertyChanged(nameof(IsAgentNode));
        OnPropertyChanged(nameof(IsKnowledgeNode));
        OnPropertyChanged(nameof(IsRuleNode));
        OnPropertyChanged(nameof(IsCodeEdit));
        RaiseLayout();
    }

    public NotesViewModel(IServiceProvider serviceProvider, IUiDispatcher dispatcher)
    {
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
        _ = LoadFontSizeAsync();
        NoteNodeVM.AnyExpandChanged += OnAnyExpandChanged;
    }

    private bool _loadingExp;
    private bool _expandSavePending;

    private void OnAnyExpandChanged()
    {
        if (_loadingExp) return;
        if (_expandSavePending) return;
        _expandSavePending = true;
        _dispatcher.Post(async () =>
        {
            _expandSavePending = false;
            await SaveExpandStateAsync();
        });
    }

    private async Task SaveExpandStateAsync()
    {
        try
        {
            var ids = new List<Guid>();
            CollectExpandedIds(Roots, ids);
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null)
                await store.SetAsync("notes.expandedNodes", JsonSerializer.Serialize(ids));
        }
        catch { /* 保存失败不影响使用 */ }
    }

    private static void CollectExpandedIds(IEnumerable<NoteNodeVM> nodes, List<Guid> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsExpanded) ids.Add(n.Id);
            CollectExpandedIds(n.Children, ids);
        }
    }

    private static void RestoreExpandState(IEnumerable<NoteNodeVM> nodes, HashSet<Guid> expanded)
    {
        foreach (var n in nodes)
        {
            n.IsExpanded = expanded.Contains(n.Id);
            RestoreExpandState(n.Children, expanded);
        }
    }

    private async Task<HashSet<Guid>?> LoadExpandStateAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null)
            {
                var json = await store.GetAsync("notes.expandedNodes");
                if (!string.IsNullOrEmpty(json))
                {
                    var ids = JsonSerializer.Deserialize<List<Guid>>(json);
                    if (ids is not null) return new HashSet<Guid>(ids);
                }
            }
        }
        catch { }
        return null;
    }

    private async Task LoadFontSizeAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null
                && double.TryParse(await store.GetAsync("notes.editorFontSize"), out var fs)
                && fs is >= 8 and <= 48)
            {
                _loadingPref = true;
                EditorFontSize = fs;
                _loadingPref = false;
            }
        }
        catch { /* 读取失败用默认值 */ }
    }

    public async Task LoadAsync()
    {
        // 表单静默保存（SaveFormItemSilentlyAsync）期间，跨 VM 订阅（WorkflowsChanged/RulesChanged/
        // SummonChanged → Notes.LoadAsync）会因保存触发的 catalog 事件而回流，导致整树重载、选中节点丢失、
        // 表单重建（焦点离开）。此时跳过——SaveFormItemSilentlyAsync 已自行回填选中节点内容供源码栏同步。
        if (_suppressExternalRefresh) return;
        try
        {
            var all = await NoteService.GetAllAsync();
            var vms = all.Select(NoteNodeVM.FromDto).ToList();
            var expanded = await LoadExpandStateAsync();

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

                // 恢复展开状态（有保存记录时才恢复，否则用默认全展开）
                if (expanded is not null)
                {
                    _loadingExp = true;
                    RestoreExpandState(vms, expanded);
                    _loadingExp = false;
                }

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
        // 表单静默保存期间跳过（同 LoadAsync 的守卫；SaveKnowledgeAsync 的显式 RefreshAsync 也由此抑制）。
        if (_suppressExternalRefresh) return;
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
            var descriptor = NoteNodeTypeRegistry.Get(type);
            var title = string.IsNullOrWhiteSpace(row.Name) ? descriptor.DefaultTitle : row.Name;

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
                Content = descriptor.DefaultContent,
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
        _saveCts?.Cancel();
        await SaveNodeInternalAsync(SelectedNode);
        _dirty = false;
        StatusMessage = "已保存";
        NotesChanged?.Invoke();
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

    /// <summary>按标题查找笔记节点（用于知识对象 link 的标题→Id 解析；重名取第一个）。</summary>
    public NoteNodeVM? FindNoteByTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return FindByTitle(Roots, title!);
        static NoteNodeVM? FindByTitle(IEnumerable<NoteNodeVM> nodes, string title)
        {
            foreach (var n in nodes)
            {
                if (string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase)) return n;
                var c = FindByTitle(n.Children, title);
                if (c != null) return c;
            }
            return null;
        }
    }

    /// <summary>按 Id 查找笔记节点（用于知识对象 link 加载时按 TargetId 反查当前标题）。</summary>
    public NoteNodeVM? FindNoteById(Guid id) => FindNode(Roots, id);

    /// <summary>收集所有可 link 的目标笔记节点（排除 <paramref name="excludeId"/> 自身），
    /// 供知识对象编辑器 target 下拉选择。不限 Knowledge——link 可指向任意笔记（普通 Note/Agent/Workflow/Knowledge），
    /// read 时原样内联目标 content，非 KO 目标在图上画成叶子节点。</summary>
    public IReadOnlyList<TargetOption> GetLinkTargets(Guid excludeId)
    {
        var result = new List<TargetOption>();
        CollectLinkTargets(Roots, excludeId, result);
        return result;
    }

    private static void CollectLinkTargets(IEnumerable<NoteNodeVM> nodes, Guid excludeId, List<TargetOption> result)
    {
        foreach (var n in nodes)
        {
            if (n.Id != excludeId)
                result.Add(new TargetOption(n.Id, n.Title));
            CollectLinkTargets(n.Children, excludeId, result);
        }
    }

    /// <summary>构建可 link 的目标节点树（排除 <paramref name="excludeId"/> 自身，保留父级与兄弟结构），
    /// 供知识对象编辑器 target 树形选择窗口按层级展开。</summary>
    public IReadOnlyList<TargetTreeNode> GetLinkTargetTree(Guid excludeId)
        => BuildTargetTree(Roots, excludeId);

    private static List<TargetTreeNode> BuildTargetTree(IEnumerable<NoteNodeVM> nodes, Guid excludeId)
    {
        var result = new List<TargetTreeNode>();
        foreach (var n in nodes)
        {
            if (n.Id == excludeId) continue;
            var children = BuildTargetTree(n.Children, excludeId);
            result.Add(new TargetTreeNode(n.Id, n.Title, children));
        }
        return result;
    }

    private static bool RemoveFromTree(ObservableCollection<NoteNodeVM> list, NoteNodeVM node)
    {
        if (list.Remove(node)) return true;
        foreach (var n in list)
            if (RemoveFromTree(n.Children, node)) return true;
        return false;
    }
}