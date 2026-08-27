using Avalonia;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>专家页视图：仅 连接器/MCP/技能 三 tab（agent/rule/workflow 的创建+编辑由 labs 知识对象页承接）。</summary>
public partial class ExpertsView : UserControl
{
    public ExpertsView()
    {
        InitializeComponent();
    }
}
