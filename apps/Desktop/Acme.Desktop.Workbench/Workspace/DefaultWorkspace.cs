using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>
/// 默认工作台定义：左 = experts tabbed，右 = chat region。
/// 纯数据——后续插件/AI 生成只需产出另一个 WorkspaceDefinition。
/// </summary>
public static class DefaultWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "default",
        Title: "默认工作台",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.Experts, PanelKeys.Experts, "连接器", "connector"),
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天右栏", "chat"),
        },
        Layout: new LayoutSplit(
            Orientation: SplitOrientation.Horizontal,
            Children: new LayoutNode[]
            {
                new LayoutTabbed(RegionIds: new[] { PanelKeys.Experts }, SelectedIndex: 0),
                new LayoutRegion(RegionId: PanelKeys.Chat),
            },
            // Pixels: [0, 420] → 内容区 Star（弹性）+ chat 区 420px 默认（首次/重置布局时）。
            // 实际拖拽位置经 WorkspaceView.Serialize 序列化到 IPreferencesStore(LayoutPrefKey) 跨重启恢复（见 WorkbenchShellViewModel.Deserialize）。
            Pixels: new double[] { 0, 420 }));
}
