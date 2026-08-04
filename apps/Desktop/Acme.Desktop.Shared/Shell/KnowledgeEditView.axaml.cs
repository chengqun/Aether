using Acme.Shell;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>知识对象结构化编辑视图。DataContext = KnowledgeEditViewModel。</summary>
public partial class KnowledgeEditView : UserControl
{
    public KnowledgeEditView() => InitializeComponent();

    private async void OnPickTarget(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is KnowledgeLinkRow row
            && DataContext is KnowledgeEditViewModel vm
            && TopLevel.GetTopLevel(this) is Window owner)
        {
            var picked = await TreePickerWindow.PickAsync(owner, vm.AvailableTargetTree, "选择目标笔记");
            if (picked is not null) row.SelectedTarget = picked;
        }
    }

    private async void OnViewGraphClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KnowledgeEditViewModel vm) return;
        var model = vm.BuildGraphModel();
        if (model.Nodes.Count == 0) return;
        var laid = WorkflowGraphBuilder.Layout(model);
        var mermaid = vm.BuildMermaid();
        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new WorkflowGraphWindow(laid, mermaid,
            "知识对象图", "节点=知识对象，箭头=typed link（标注=role），高亮=当前对象");
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }
}
