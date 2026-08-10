using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>
/// 默认工作台定义：左 = notes/experts tabbed，右 = chat region。
/// 纯数据——后续插件/AI 生成只需产出另一个 WorkspaceDefinition。
/// </summary>
public static class DefaultWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "default",
        Title: "默认工作台",
        Panels: new List<PanelDescriptor>
        {
            new("notes", "notes", "笔记", "notes"),
            new("experts", "experts", "专家", "experts"),
            new("chat", "chat", "聊天", "chat"),
        },
        Layout: new LayoutSplit(
            Orientation: SplitOrientation.Horizontal,
            Children: new LayoutNode[]
            {
                new LayoutTabbed(RegionIds: new[] { "notes", "experts" }, SelectedIndex: 0),
                new LayoutRegion(RegionId: "chat"),
            },
            // Pixels: [0, 420] → 内容区 Star（弹性）+ chat 区 420px 固定（可拖 GridSplitter 改，不跨重启持久）。
            Pixels: new double[] { 0, 420 }));
}
