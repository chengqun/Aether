using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;
using Acme.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

public partial class NotesView : UserControl
{
    private NoteNodeVM? _dragSource;
    private NoteNodeVM? _dropTarget;
    private Point _pressPos;
    private bool _isDragging;
    private const double DragThreshold = 4.0;

    private NotesViewModel? _vm;
    private NoteNodeVM? _subscribedNode;
    private bool _syncing;
    private FoldingManager? _foldingManager;

    public NotesView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.PointerWheelChanged += OnCodeEditorWheelChanged;
        // 隧道 KeyDown：在 AvaloniaEdit 默认粘贴之前接管 markdown 节点的 Ctrl/Cmd+V（图片→本地存档+markdown 引用）
        CodeEditor.AddHandler(InputElement.KeyDownEvent, OnCodeEditorPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        CodeEditor.Loaded += (_, _) =>
        {
            ConfigureEditorAppearance();
            SyncCodeEditor();
        };
        this.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        this.Loaded += (_, _) => RestoreLayout();
    }

    // —— 分隔条位置持久化（目录树宽度 + 上下预览/编辑比例）——

    /// <summary>恢复笔记分隔条位置。JsonPreferencesStore.GetAsync 同步完成，Loaded 期直接读。</summary>
    private void RestoreLayout()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("notes.treeW").GetAwaiter().GetResult(), out var tw)
                && tw is >= 120 and <= 600)
                TreeGrid.ColumnDefinitions[0].Width = new GridLength(tw);
            var ps = double.TryParse(store.GetAsync("notes.previewStar").GetAwaiter().GetResult(), out var p) ? p : 1d;
            var es = double.TryParse(store.GetAsync("notes.editorStar").GetAwaiter().GetResult(), out var e) ? e : 1d;
            if (ps <= 0) ps = 1d;
            if (es <= 0) es = 1d;
            SplitGrid.RowDefinitions[0].Height = new GridLength(ps, GridUnitType.Star);
            SplitGrid.RowDefinitions[2].Height = new GridLength(es, GridUnitType.Star);
        }
        catch { /* 读取失败用默认分隔 */ }
    }

    /// <summary>采集当前分隔尺寸，供主窗口关闭时统一持久化（读 GridLength.Value，隐藏页也不丢）。</summary>
    public (double TreeWidth, double PreviewStar, double EditorStar) CollectLayout()
        => (TreeGrid.ColumnDefinitions[0].Width.Value,
            SplitGrid.RowDefinitions[0].Height.Value,
            SplitGrid.RowDefinitions[2].Height.Value);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as NotesViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncCodeEditor();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(NotesViewModel.SelectedNode))
        {
            if (_subscribedNode != null) _subscribedNode.PropertyChanged -= OnNodePropertyChanged;
            _subscribedNode = _vm.SelectedNode;
            if (_subscribedNode != null) _subscribedNode.PropertyChanged += OnNodePropertyChanged;
            _ = CreateEditVmForNodeAsync();
            SyncCodeEditor();
        }
        else if (e.PropertyName == nameof(NotesViewModel.IsCodeEdit) && _vm.IsCodeEdit)
        {
            SyncCodeEditor();
        }
    }

    private MainViewModelBase? MainVm => (TopLevel.GetTopLevel(this)?.DataContext) as MainViewModelBase;

    /// <summary>选中 Agent/Workflow 时创建对应表单 VM；保存后 RefreshAsync 同步源码。</summary>
    private async Task CreateEditVmForNodeAsync()
    {
        var node = _vm?.SelectedNode;
        if (node is null || _vm is null) return;
        var main = MainVm;
        if (main is null) return;

        if (node.NodeType == NoteNodeType.Agent)
        {
            var expert = main.Experts.Experts.FirstOrDefault(e => e.Id == node.Id);
            if (expert is null) return;
            var editVm = main.Experts.CreateEditViewModel(expert);
            if (editVm is null) return;
            editVm.SaveRequested += async saved =>
            {
                var result = await main.Experts.SaveAsync(saved);
                if (result.Success) { _vm.StatusMessage = "已保存"; await _vm.RefreshAsync(); }
                else editVm.Error = result.Error;
            };
            // 自动保存（含 per-tool 权限调整）：静默落库（不重载树）+ 同步下栏源码（含 tool_approvals）。
            editVm.AutoSaveRequested += async saved =>
            {
                var result = await _vm.SaveFormItemSilentlyAsync(() => main.Experts.SaveAsync(saved), saved.Id);
                if (result.Success) SyncCodeEditor();
            };
            editVm.CancelRequested += () => { _vm.IsPreviewOnly = false; };
            _vm.ActiveEditVm = editVm;
        }
        else if (node.NodeType == NoteNodeType.Rule)
        {
            var rule = main.Rules.Rules.FirstOrDefault(r => r.Id == node.Id);
            if (rule is null) return;
            var editVm = main.Rules.CreateEditViewModel(rule);
            if (editVm is null) return;
            editVm.SaveRequested += async saved =>
            {
                var result = await main.Rules.SaveAsync(saved);
                if (result.Success) { _vm.StatusMessage = "已保存"; await _vm.RefreshAsync(); }
                else editVm.Error = result.Error;
            };
            // 自动保存（含 per-tool 权限调整）：静默落库（不重载树）+ 同步下栏源码（含 tool_approvals）。
            editVm.AutoSaveRequested += async saved =>
            {
                var result = await _vm.SaveFormItemSilentlyAsync(() => main.Rules.SaveAsync(saved), saved.Id);
                if (result.Success) SyncCodeEditor();
            };
            editVm.CancelRequested += () => { _vm.IsPreviewOnly = false; };
            _vm.ActiveEditVm = editVm;
        }
        else if (node.NodeType == NoteNodeType.CodeRule)
        {
            var rule = main.Rules.Rules.FirstOrDefault(r => r.Id == node.Id);
            if (rule is null) return;
            var editVm = main.Rules.CreateCodeRuleEditViewModel(rule);
            editVm.SaveRequested += async saved =>
            {
                // 下栏源码可能改了 C# body：从当前节点 content 重新取 body，避免表单保存用 VM 快照覆盖下栏改动
                if (_vm?.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
                    saved.SystemPrompt = _vm.CodeBodyFromContent(c);
                var result = await main.Rules.SaveAsync(saved);
                if (result.Success) { _vm.StatusMessage = "已保存"; await _vm.RefreshAsync(); }
                else editVm.Error = result.Error;
            };
            // 自动保存（含 per-tool 权限调整）：静默落库（不重载树）+ 同步下栏源码（含 tool_approvals）。
            // 同样从 live content 回填 body——否则调工具/改权限触发的自动保存会把 C# 源码重置成旧快照。
            editVm.AutoSaveRequested += async saved =>
            {
                if (_vm?.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
                    saved.SystemPrompt = _vm.CodeBodyFromContent(c);
                var result = await _vm.SaveFormItemSilentlyAsync(() => main.Rules.SaveAsync(saved), saved.Id);
                if (result.Success) SyncCodeEditor();
            };
            editVm.CancelRequested += () => { _vm.IsPreviewOnly = false; };
            _vm.ActiveEditVm = editVm;
        }
        else if (node.NodeType == NoteNodeType.Workflow)
        {
            var wf = main.Workflows.Workflows.FirstOrDefault(w => w.Id == node.Id);
            if (wf is null) return;
            var editVm = await main.Workflows.CreateEditViewModelAsync(wf);
            if (editVm is null) return;
            editVm.SaveRequested += async saved =>
            {
                var result = await main.Workflows.SaveAsync(saved);
                if (result.Success) { _vm.StatusMessage = "已保存"; await _vm.RefreshAsync(); }
                else editVm.Error = result.Error;
            };
            // 自动保存：静默落库（不重载树）+ 同步下栏源码。
            editVm.AutoSaveRequested += async saved =>
            {
                var result = await _vm.SaveFormItemSilentlyAsync(() => main.Workflows.SaveAsync(saved), saved.Id);
                if (result.Success) SyncCodeEditor();
            };
            editVm.CancelRequested += () => { _vm.IsPreviewOnly = false; };
            _vm.ActiveEditVm = editVm;
        }
        else if (node.NodeType == NoteNodeType.Knowledge)
        {
            var (def, parseErr) = _vm.TryParseKnowledge(node.Content ?? "");
            if (def is null)
            {
                _vm.StatusMessage = "解析失败：" + parseErr;
                return;
            }
            var editVm = new KnowledgeEditViewModel(node.Id, node.Title, def,
                _vm?.GetLinkTargets(node.Id) ?? Array.Empty<TargetOption>(),
                _vm?.GetLinkTargetTree(node.Id) ?? Array.Empty<TargetTreeNode>(),
                title => _vm?.FindNoteByTitle(title) is { } tn ? (tn.Id, tn.Title) : ((Guid Id, string Title)?)null,
                id => _vm?.FindNoteById(id)?.Title,
                title => _vm?.FindNoteByTitle(title) is { } tn2
                    ? (tn2.Title, _vm.TryParseKnowledge(tn2.Content ?? "").Def)
                    : ((string Title, KnowledgeObjectDefinition? Def)?)null);
            editVm.SaveRequested += async saved =>
            {
                // debounce 自动保存可能跨越节点切换：仅当当前选中节点仍是本对象时才写库
                if (_vm?.SelectedNode?.Id != editVm.Id) return;
                // 下栏源码可能改了 body：从当前节点 content 重新解析 body，避免表单保存覆盖下栏改动
                if (_vm?.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
                {
                    var (redef, _) = _vm.TryParseKnowledge(c);
                    if (redef is not null) saved.Body = redef.Body;
                }
                // 静默落库（不重载树）+ 同步下栏源码（含 kind/links/body 改动）
                var result = await _vm.SaveFormItemSilentlyAsync(() => _vm.SaveKnowledgeAsync(saved, editVm.Name), editVm.Id);
                if (result.Success) { _vm.StatusMessage = "已保存"; SyncCodeEditor(); }
                else editVm.Error = result.Error;
            };
            editVm.CancelRequested += () => { _vm.IsPreviewOnly = false; };
            _vm.ActiveEditVm = editVm;
        }
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 语法变了 → 重设高亮 + 刷新 markdown 预览栏可见性
        if (e.PropertyName == nameof(NoteNodeVM.Syntax))
            ApplyHighlighting();
        if (e.PropertyName is nameof(NoteNodeVM.Syntax) or nameof(NoteNodeVM.IsMarkdown))
            _vm?.RaiseIsMarkdownNode();
    }

    /// <summary>把节点内容载入编辑器并按语法设高亮。</summary>
    private void SyncCodeEditor()
    {
        if (_vm?.SelectedNode is not { } node) return;
        // Document 在控件模板应用（首次显示）后才创建；未就绪时延迟到下一帧再试。
        if (CodeEditor.Document is null)
        {
            Dispatcher.UIThread.Post(SyncCodeEditor);
            return;
        }
        _syncing = true;
        try { CodeEditor.Text = node.Content ?? ""; }
        finally { _syncing = false; }
        ApplyHighlighting();
        UpdateFoldings();
    }

    private void ApplyHighlighting()
    {
        if (_vm?.SelectedNode is not { } node) return;
        CodeEditor.SyntaxHighlighting = HighlightingFor(node);
    }

    /// <summary>节点 → 高亮定义：类型强制语法优先（Agent=YAML/Workflow=Json），否则按节点语法映射。统一 VSCode 配色。</summary>
    private static IHighlightingDefinition? HighlightingFor(NoteNodeVM node)
    {
        var forced = NoteNodeTypeRegistry.Get(node.NodeType).Syntax;
        var syntax = (forced ?? node.Syntax)?.ToLowerInvariant();
        if (syntax == "yaml") return LoadYaml();
        return Load(syntax switch
        {
            "markdown" => "MarkDown",
            "sql" => "TSQL",
            "python" => "Python",
            "javascript" => "JavaScript",
            "json" => "Json",
            "csharp" => "C#",
            "html" => "HTML",
            "css" => "CSS",
            "xml" => "XML",
            "java" => "Java",
            "cpp" => "C++",
            "vb" => "VB",
            "powershell" => "PowerShell",
            _ => null,
        });
    }

    private static IHighlightingDefinition? _yamlDef;

    /// <summary>自定义 YAML 高亮（AvaloniaEdit 无内置；内联 .xshd，VSCode 配色）。</summary>
    private static IHighlightingDefinition LoadYaml()
    {
        if (_yamlDef is not null) return _yamlDef;
        var xshd = @"<SyntaxDefinition name=""YAML"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#008000""/>
  <Color name=""String"" foreground=""#A31515""/>
  <Color name=""Number"" foreground=""#098658""/>
  <Color name=""Keyword"" foreground=""#0000FF""/>
  <Color name=""Key"" foreground=""#267F99""/>
  <Color name=""DocMarker"" foreground=""#808080""/>
  <RuleSet>
    <Rule color=""DocMarker"">^---\s*$</Rule>
    <Rule color=""DocMarker"">^\.\.\.\s*$</Rule>
    <Rule color=""Comment"">\#.*</Rule>
    <Rule color=""Key"">^\s*[\w-]+(?=\s*:)</Rule>
    <Rule color=""Key"">\b[\w-]+(?=\s*:)</Rule>
    <Span color=""String""><Begin>&quot;</Begin><End>&quot;</End></Span>
    <Span color=""String""><Begin>'</Begin><End>'</End></Span>
    <Rule color=""Number"">\b\d+(\.\d+)?\b</Rule>
    <Keywords color=""Keyword"">
      <Word>true</Word><Word>false</Word><Word>null</Word>
      <Word>yes</Word><Word>no</Word><Word>on</Word><Word>off</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd));
        _yamlDef = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _yamlDef;
    }

    private static IHighlightingDefinition? Load(string? name)
    {
        if (name is null) return null;
        var def = HighlightingManager.Instance.GetDefinition(name);
        if (def is not null) ApplyVscodeColors(def);
        return def;
    }

    /// <summary>VSCode 质感：当前行高亮 + 选中色 + 括号匹配 + 搜索面板。</summary>
    private void ConfigureEditorAppearance()
    {
        try
        {
            CodeEditor.Options.HighlightCurrentLine = true;
            var tv = CodeEditor.TextArea.TextView;
            tv.CurrentLineBackground = new SolidColorBrush(Color.Parse("#F0F0F4"));
            tv.CurrentLineBorder = null;
            CodeEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#ADD6FF"));
            CodeEditor.TextArea.SelectionForeground = new SolidColorBrush(Color.Parse("#1A1A1F"));
            // 安装搜索替换面板（Ctrl+F 呼出）
            SearchPanel.Install(CodeEditor);
            // 安装代码折叠（基于 {} 配对）
            _foldingManager = FoldingManager.Install(CodeEditor.TextArea);
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>把内置定义的颜色重映射为 VSCode Light+ 配色（蓝关键字/橙字符串/绿注释/绿数字）。</summary>
    private static void ApplyVscodeColors(IHighlightingDefinition def)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            var n = (c.Name ?? "").ToUpperInvariant();
            string? hex = null;
            if (n.Contains("COMMENT") || n.Contains("PREPROCESSOR") || n.Contains("DOC")) hex = "#008000";
            else if (n.Contains("STRING") || n.Contains("CHAR")) hex = "#A31515";
            else if (n.Contains("NUMBER") || n.Contains("DIGIT")) hex = "#098658";
            else if (n.Contains("METHOD") || n.Contains("CALL") || n.Contains("FUNCTION")) hex = "#795E26";
            else if (n.Contains("TYPE") || n.Contains("CLASS")) hex = "#267F99";
            else if (n.Contains("TAG") || n.Contains("ELEMENT")) hex = "#800000";
            else if (n.Contains("ATTRIBUT")) hex = "#FF0000";
            else if (n.Contains("KEYWORD") || n.Contains("VISIBILITY") || n.Contains("MODIFIER")
                || n.Contains("REFERENCE") || n.Contains("VALUE") || n.Contains("EXCEPTION")
                || n.Contains("GOTO") || n.Contains("CONTEXT") || n.Contains("OPERATOR")
                || n.Contains("PARAMETER") || n.Contains("NAMESPACE") || n.Contains("GETSET")
                || n.Contains("TRUEFALSE") || n.Contains("SEMANTIC") || n.Contains("CHECKED")
                || n.Contains("UNSAFE") || n.Contains("DECLARATION")) hex = "#0000FF";
            if (hex is not null)
            {
                c.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
                c.FontWeight = null; // VSCode 关键字不加粗
            }
        }
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        if (_vm?.SelectedNode is { } node)
            node.Content = CodeEditor.Text ?? "";
        _vm?.MarkDirty(); // 启动延迟自动保存
        // 知识对象：下栏源码改了 body → 实时 reparse 同步到编辑器 VM，刷新 markdown 渲染
        if (_vm?.IsKnowledgeNode == true && _vm.ActiveEditVm is KnowledgeEditViewModel kevm)
        {
            var (redef, _) = _vm.TryParseKnowledge(CodeEditor.Text ?? "");
            if (redef is not null) kevm.SyncBody(redef.Body);
        }
        UpdateFoldings();
    }

    private bool _pasting;

    /// <summary>markdown 节点接管粘贴：剪贴板有图片→存本地+插 markdown 引用；否则文本回退。非 markdown 节点不处理。</summary>
    private void OnCodeEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.IsMarkdownNode != true || _pasting) return;
        if (e.Key != Key.V || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        e.Handled = true; // 阻断 AvaloniaEdit 默认粘贴，由我们 async 处理
        _ = HandlePasteAsync();
    }

    private async Task HandlePasteAsync()
    {
        if (_pasting) return;
        _pasting = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(CodeEditor)?.Clipboard;
            if (clipboard is null) return;
            // 图片（截图等）→ 存本地 + 插 markdown 引用
            var bmp = await clipboard.TryGetBitmapAsync();
            if (bmp is not null) { SaveBitmapAndInsert(bmp); return; }
            // 文本回退：保持 markdown 节点正常粘贴文本
            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text)) InsertText(text);
        }
        finally { _pasting = false; }
    }

    private void SaveBitmapAndInsert(Bitmap bmp)
    {
        var root = _vm?.ImageRoot;
        if (string.IsNullOrEmpty(root)) { if (_vm is not null) _vm.StatusMessage = "未配置图片目录"; return; }
        try
        {
            Directory.CreateDirectory(root);
            var noteId = _vm?.SelectedNode?.Id.ToString("N") ?? "note";
            var fileName = $"{noteId}-{Guid.NewGuid():N}.png";
            bmp.Save(Path.Combine(root, fileName)); // .png 扩展名 → PNG 编码
            InsertText($"![图片]({fileName})");
        }
        catch (Exception ex)
        {
            if (_vm is not null) _vm.StatusMessage = $"图片保存失败：{ex.Message}";
        }
    }

    /// <summary>在选区/光标处插入文本（接管粘贴的插入路径，触发 TextChanged→自动落库）。</summary>
    private void InsertText(string text)
    {
        var area = CodeEditor.TextArea;
        var doc = CodeEditor.Document;
        if (area is null || doc is null) return;
        int off;
        var seg = area.Selection.SurroundingSegment;
        if (seg is { Length: > 0 })
        {
            doc.Replace(seg.Offset, seg.Length, text);
            off = seg.Offset + text.Length;
        }
        else
        {
            off = area.Caret.Offset;
            doc.Insert(off, text);
            off += text.Length;
        }
        area.Caret.Offset = off;
        area.ClearSelection();
        area.Caret.BringCaretToView();
    }

    /// <summary>按语法类型生成折叠区域：大括号语言折叠 {}、SQL 折叠 ()、YAML 折叠 --- 文档段。</summary>
    private void UpdateFoldings()
    {
        if (_foldingManager is null) return;
        var text = CodeEditor.Document.Text;
        var foldings = new List<NewFolding>();

        var nodeType = _vm?.SelectedNode?.NodeType ?? NoteNodeType.Note;
        var forced = NoteNodeTypeRegistry.Get(nodeType).Syntax;
        var kind = forced ?? _vm?.SelectedNode?.Syntax?.ToLowerInvariant();

        if (kind == "yaml")
            FoldYamlDocuments(text, foldings);
        else if (kind == "sql")
            FoldPairs(text, foldings, '(', ')');
        else
            FoldPairs(text, foldings, '{', '}'); // C 风格 + JSON

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _foldingManager.UpdateFoldings(foldings, -1);
    }

    /// <summary>配对字符折叠（{} 或 ()），超过 2 字符才折。</summary>
    private static void FoldPairs(string text, List<NewFolding> foldings, char open, char close)
    {
        var starts = new Stack<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == open) starts.Push(i);
            else if (text[i] == close && starts.Count > 0)
            {
                var start = starts.Pop();
                if (i - start > 2)
                    foldings.Add(new NewFolding(start, i + 1));
            }
        }
    }

    /// <summary>YAML 文档段折叠：--- 到下一个 ---/... 之间。</summary>
    private static void FoldYamlDocuments(string text, List<NewFolding> foldings)
    {
        var marks = new List<int>();
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                var line = text.AsSpan(lineStart, i - lineStart).Trim('\r').Trim();
                if (line is "---" or "...")
                    marks.Add(lineStart);
                lineStart = i + 1;
            }
        }
        for (var i = 0; i + 1 < marks.Count; i++)
        {
            if (marks[i + 1] - marks[i] > 4)
                foldings.Add(new NewFolding(marks[i], marks[i + 1]));
        }
    }

    /// <summary>Ctrl+滚轮缩放编辑器字体（VSCode 风，8~48，持久化）。</summary>
    private void OnCodeEditorWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (_vm is null) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var size = Math.Max(8, Math.Min(48, _vm.EditorFontSize + (delta > 0 ? 1 : -1)));
        if (Math.Abs(size - _vm.EditorFontSize) < 0.01) return;
        _vm.EditorFontSize = size; // 绑定更新 CodeEditor.FontSize，并持久化
        e.Handled = true;
    }

    private async void OnCreateSiblingClick(object? sender, RoutedEventArgs e)
        => await OpenNewNodeDialogAsync(asChild: false, header: "📝 新建节点");

    private async void OnCreateChildClick(object? sender, RoutedEventArgs e)
        => await OpenNewNodeDialogAsync(asChild: true, header: "📝 新建子节点");

    private async Task OpenNewNodeDialogAsync(bool asChild, string header)
    {
        if (!TryGetVmParent(out var vm, out var parent)) return;
        var row = vm.BuildNewNodeRow();
        var dialog = new NoteNodeEditWindow { DataContext = row };
        dialog.SetHeader(header);
        if (await dialog.ShowDialog<bool>(parent))
            await vm.ApplyNewNodeAsync(row, asChild);
    }

    private async void OnEditPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetVmParent(out var vm, out var parent)) return;
        var row = vm.BuildEditNodeRow();
        if (row == null) return;
        var dialog = new NoteNodeEditWindow { DataContext = row };
        dialog.SetHeader("✏️ 修改节点属性");
        if (await dialog.ShowDialog<bool>(parent))
            await vm.ApplyEditNodeAsync(row);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetVmParent(out var vm, out var parent)) return;
        if (vm.SelectedNode == null) return;
        var title = vm.SelectedNode.Title;
        var ok = await ConfirmAsync(parent, "删除节点", $"确定删除「{title}」？其子节点将提升为根节点。", "删除");
        if (ok)
            await vm.DeleteSelectedAsync();
    }

    /// <summary>简易确认对话框（内联构建，无需额外 axaml）。返回 true=点了确认按钮。</summary>
    private static async Task<bool> ConfirmAsync(Window parent, string title, string message, string confirmText)
    {
        var result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 360, Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var root = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 8) };
        var okBtn = new Button { Content = confirmText, Padding = new Thickness(20, 8) };
        okBtn.Classes.Add("primary");
        btns.Children.Add(cancel);
        btns.Children.Add(okBtn);
        root.Children.Add(btns);
        dlg.Content = root;
        cancel.Click += (_, _) => dlg.Close();
        okBtn.Click += (_, _) => { result = true; dlg.Close(); };
        await dlg.ShowDialog(parent);
        return result;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetDragState();
        if (!e.GetCurrentPoint(NotesTree).Properties.IsLeftButtonPressed) return;
        var node = ResolveNode(e.Source as Visual, e.GetPosition(NotesTree));
        if (node == null) return;
        _dragSource = node;
        _pressPos = e.GetPosition(NotesTree);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource == null) return;
        if (!e.GetCurrentPoint(NotesTree).Properties.IsLeftButtonPressed) { ResetDragState(); return; }

        if (!_isDragging)
        {
            var pos = e.GetPosition(NotesTree);
            if (Math.Abs(pos.X - _pressPos.X) + Math.Abs(pos.Y - _pressPos.Y) < DragThreshold) return;
            _isDragging = true;
            SetStatus($"拖拽「{_dragSource.Title}」— 移到目标节点上松手放入（空白处提升为根）");
            NotesTree.Cursor = new Cursor(StandardCursorType.DragMove);
        }

        var target = OverTree(e.GetPosition(NotesTree))
            ? ResolveNode(e.Source as Visual, e.GetPosition(NotesTree))
            : null;
        UpdateDropTarget(target);
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var source = _dragSource;
        var wasDragging = _isDragging;
        var target = _dropTarget;
        var overTree = OverTree(e.GetPosition(NotesTree));
        ResetDragState();

        if (!wasDragging || source == null) return;
        if (DataContext is not NotesViewModel vm) return;

        if (!overTree) { SetStatus("拖拽已取消（释放在树外）"); return; }
        if (ReferenceEquals(target, source) || (target != null && IsDescendant(source, target)))
        { SetStatus("不能移到自身或其子节点下"); return; }

        await vm.MoveNodeAsync(source.Id, target?.Id);
    }

    private void ResetDragState()
    {
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = null;
        if (_isDragging) NotesTree.Cursor = null;
        _dragSource = null;
        _isDragging = false;
    }

    private void UpdateDropTarget(NoteNodeVM? target)
    {
        if (target == _dropTarget) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        if (target != null && _dragSource != null &&
            (ReferenceEquals(target, _dragSource) || IsDescendant(_dragSource, target)))
            target = null;
        _dropTarget = target;
        if (target != null) target.IsDropTarget = true;
    }

    private bool OverTree(Point pos) =>
        pos.X >= 0 && pos.Y >= 0 &&
        pos.X <= NotesTree.Bounds.Width && pos.Y <= NotesTree.Bounds.Height;

    private bool TryGetVmParent(out NotesViewModel vm, out Window parent)
    {
        vm = DataContext as NotesViewModel;
        parent = TopLevel.GetTopLevel(this) as Window;
        return vm != null && parent != null;
    }

    private void SetStatus(string text)
    {
        if (DataContext is NotesViewModel vm) vm.StatusMessage = text;
    }

    private NoteNodeVM? ResolveNode(Visual? src, Point pos)
    {
        var n = FindNodeAt(NotesTree.InputHitTest(pos) as Visual);
        if (n != null) return n;
        return FindNodeAt(src);
    }

    private static NoteNodeVM? FindNodeAt(Visual? v)
    {
        while (v != null)
        {
            if (v is TreeViewItem tvi && tvi.DataContext is NoteNodeVM n) return n;
            v = v.GetVisualParent() as Visual;
        }
        return null;
    }

    private static bool IsDescendant(NoteNodeVM ancestor, NoteNodeVM candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate) || IsDescendant(child, candidate)) return true;
        }
        return false;
    }
}