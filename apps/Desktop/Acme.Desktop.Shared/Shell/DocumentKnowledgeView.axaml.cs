using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档版知识对象表单视图（镜像 <see cref="KnowledgeEditView"/>，去 note 耦合）。
/// code-behind 极简：无 note 选择器/图/跳转（link target 为文本）。DataContext 由
/// <see cref="DocumentsEditorView"/> 上栏知识表单宿主设为 <see cref="Acme.Desktop.Mvvm.DocumentKnowledgeViewModel"/>。
/// </summary>
public partial class DocumentKnowledgeView : UserControl
{
    public DocumentKnowledgeView()
    {
        InitializeComponent();
    }
}
