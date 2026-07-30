using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using Acme.Notes.Domain;
using Acme.Shell;

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

    public NotesView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.PointerWheelChanged += OnCodeEditorWheelChanged;
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
    }

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
            // 重新订阅当前节点的 Syntax 变化（切高亮）
            if (_subscribedNode != null) _subscribedNode.PropertyChanged -= OnNodePropertyChanged;
            _subscribedNode = _vm.SelectedNode;
            if (_subscribedNode != null) _subscribedNode.PropertyChanged += OnNodePropertyChanged;
            SyncCodeEditor();
        }
        else if (e.PropertyName == nameof(NotesViewModel.IsCodeEdit) && _vm.IsCodeEdit)
        {
            SyncCodeEditor();
        }
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 语法变了 → 重设高亮（内容同步由 TextChanged 回写，无需重载）
        if (e.PropertyName == nameof(NoteNodeVM.Syntax))
            ApplyHighlighting();
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
    }

    private void ApplyHighlighting()
    {
        if (_vm?.SelectedNode is not { } node) return;
        CodeEditor.SyntaxHighlighting = HighlightingFor(node.Syntax);
    }

    /// <summary>VSCode 质感：当前行高亮 + 选中色。</summary>
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
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>语法 key → AvaloniaEdit 内置高亮定义；text/未知 → null（纯文本）。统一重映射为 VSCode 配色。</summary>
    private static IHighlightingDefinition? HighlightingFor(string? syntax)
    {
        var name = syntax?.ToLowerInvariant() switch
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
        };
        if (name is null) return null;
        var def = HighlightingManager.Instance.GetDefinition(name);
        if (def is not null) ApplyVscodeColors(def);
        return def;
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
    }

    /// <summary>Ctrl+滚轮缩放编辑器字体（VSCode 风，8~48）。</summary>
    private void OnCodeEditorWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var size = Math.Max(8, Math.Min(48, CodeEditor.FontSize + (delta > 0 ? 1 : -1)));
        if (Math.Abs(size - CodeEditor.FontSize) < 0.01) return;
        CodeEditor.FontSize = size;
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

    private async void OnEditAgentClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetVmParent(out var vm, out var parent)) return;
        if (parent.DataContext is not MainViewModelBase main) return;

        var selectedNode = vm.SelectedNode;
        if (selectedNode == null || selectedNode.NodeType != NoteNodeType.Agent) return;

        var expert = main.Experts.Experts.FirstOrDefault(e => e.Id == selectedNode.Id);
        var editVm = main.Experts.CreateEditViewModel(expert);
        if (editVm == null) return;

        var dialog = new ExpertEditWindow { DataContext = editVm };

        editVm.SaveRequested += async saved =>
        {
            var result = await main.Experts.SaveAsync(saved);
            if (result.Success)
            {
                dialog.Close();
                await vm.LoadAsync();
            }
            else
            {
                editVm.Error = result.Error;
            }
        };
        editVm.CancelRequested += () => dialog.Close();

        await dialog.ShowDialog(parent);
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