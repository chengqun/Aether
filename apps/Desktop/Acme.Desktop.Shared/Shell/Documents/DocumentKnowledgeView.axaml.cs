using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档版知识对象表单视图（镜像 <see cref="KnowledgeEditView"/>，去 note 耦合）。
/// code-behind 仅图按钮（link target 为文本，无 note 选择器/跳转）——
/// <see cref="OnViewGraphClick"/> 后台线程构建闭包图（resolveTargetDef 读 .ok 文件，文件 I/O 不阻塞 UI；
/// note 版内存 note 不读文件故同步，文档版必须 Task.Run），弹 <see cref="WorkflowGraphWindow"/>。
/// DataContext 由 <see cref="DocumentsEditorView"/> 上栏知识表单宿主设为 <see cref="DocumentKnowledgeViewModel"/>。
/// </summary>
public partial class DocumentKnowledgeView : UserControl
{
    public DocumentKnowledgeView()
    {
        InitializeComponent();
    }

    private async void OnViewGraphClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentKnowledgeViewModel vm) return;
        // resolveTargetDef 读 .ok 文件（文件 I/O）→ 后台线程构建图 + 布局 + Mermaid，避免 UI 卡顿。
        LaidOutGraph? laid = null;
        string mermaid = string.Empty;
        await Task.Run(() =>
        {
            var model = vm.BuildGraphModel();
            if (model.Nodes.Count == 0) return;
            laid = WorkflowGraphBuilder.Layout(model);
            mermaid = WorkflowGraphBuilder.ToMermaid(model);
        });
        if (laid is not { } graph) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var win = new WorkflowGraphWindow(graph, mermaid,
            "文档知识对象图", "节点=知识对象（名称），箭头=typed link（标注=role），高亮=当前对象");
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }
}
