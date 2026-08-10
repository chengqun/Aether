using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>树形选择弹窗：按层级展开选一个目标节点，确定返回 TargetOption，取消返回 null。</summary>
public partial class TreePickerWindow : ThemedWindow
{
    private TargetTreeNode? _selected;
    private TaskCompletionSource<TargetOption?>? _tcs;

    public TreePickerWindow() { InitializeComponent(); } // 设计器

    private TreePickerWindow(IReadOnlyList<TargetTreeNode> tree, string title)
    {
        InitializeComponent();
        Title = title;
        Tree.ItemsSource = tree;
    }

    /// <summary>模态弹出树形选择，返回选中的节点（取消/关闭返回 null）。</summary>
    public static async Task<TargetOption?> PickAsync(Window owner, IReadOnlyList<TargetTreeNode> tree, string title)
    {
        var w = new TreePickerWindow(tree, title);
        var tcs = new TaskCompletionSource<TargetOption?>();
        w._tcs = tcs;
        w.Closed += (_, _) => tcs.TrySetResult(null); // 未确定就关闭 → null
        await w.ShowDialog(owner);
        return await tcs.Task;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = Tree.SelectedItem as TargetTreeNode;
        ConfirmBtn.IsEnabled = _selected is not null;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } n)
            _tcs?.TrySetResult(new TargetOption(n.Id, n.Title));
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _tcs?.TrySetResult(null);
        Close();
    }
}
