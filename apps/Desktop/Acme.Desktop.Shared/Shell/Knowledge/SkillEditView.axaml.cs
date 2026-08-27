using Acme.Desktop.Mvvm;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 技能（<c>SKILL.md</c>）表单视图。镜像 <see cref="DocumentKnowledgeView"/>，去 links/图（技能无 typed 图边）。
/// 无 code-behind 逻辑——双向同步在宿主 <see cref="KnowledgeObjectsView"/>（<c>KnowledgeSourceUpdated</c> /
/// <c>SyncFromSource</c>，同知识对象路径）。DataContext 由 <see cref="KnowledgeObjectsViewModel.ActiveEditVm"/>
/// 设为 <see cref="SkillEditViewModel"/>。
/// </summary>
public partial class SkillEditView : UserControl
{
    public SkillEditView()
    {
        InitializeComponent();
    }
}
