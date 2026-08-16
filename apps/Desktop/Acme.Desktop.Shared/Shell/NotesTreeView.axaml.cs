using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>笔记树（从原 NotesView col0 拆出）：TreeView + ContextMenu + 拖拽改父级。
/// shell 级独立左栏，notes tab 时由 LeftTreePanelView 切显。与 NotesEditorView 共享同一 NotesViewModel 单例——
/// 树选节点 → VM.SelectedNode 变 → 编辑器响应。无树宽持久化（树宽由外层 LayoutSplit 经 workbench.layout 持久化）。</summary>
public partial class NotesTreeView : UserControl
{
    private NoteNodeVM? _dragSource;
    private NoteNodeVM? _dropTarget;
    private Point _pressPos;
    private bool _isDragging;
    private const double DragThreshold = 4.0;

    public NotesTreeView()
    {
        InitializeComponent();
        // 树内拖拽改父级：隧道/冒泡指针事件绑到本 view（命中测试经 NotesTree.InputHitTest）。
        this.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
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
