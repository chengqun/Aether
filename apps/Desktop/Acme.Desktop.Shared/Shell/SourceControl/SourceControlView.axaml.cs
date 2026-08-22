using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>源代码管理页 code-behind。逻辑全在 <see cref="SourceControlViewModel"/> 绑定；本类仅架 UserControl。
/// DataContext 由 WorkbenchViewRegistry 设为 <see cref="SourceControlViewModel"/>。</summary>
public partial class SourceControlView : UserControl
{
    public SourceControlView()
    {
        InitializeComponent();
    }
}
