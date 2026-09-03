using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Qifu.Desktop;

/// <summary>
/// Qifu 租户工作台定义：外层 = 水平 3 分割（侧栏 | 上下分割[主面板 tabbed + 终端] | chat）。
/// 侧栏是 shell 级独立左栏（VSCode 式：占满高度，终端只压编辑器区不压树）；是 LayoutRegion 不进 LayoutTabbed → 不进 nav rail，
/// 叠放文件树 + SCM 面板 + .md 树，documents/sourcecontrol/knowledge tab 选中时可见并切对应树（WorkspaceView 按 SelectedTabIndex 切列宽 0/恢复 + SetActiveTree）。
/// nav 5 项数据驱动自 LayoutTabbed——终端在底部 region 不进 nav（VSCode 式底部面板：agent run_in_terminal 时
/// 终端与上方主面板同时可见，不用切 tab）。无租户专属页（无 CTP/回测/股票）。
/// icon 用 icons.json manifest key（IconLoader 解析为 path data）。
/// </summary>
public static class QifuWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "qifu",
        Title: "Qifu 工作台",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.LeftTree, PanelKeys.LeftTree, "侧栏", "files"),
            new(PanelKeys.Documents, PanelKeys.Documents, "文档", "documents"),
            new(PanelKeys.SourceControl, PanelKeys.SourceControl, "源代码管理", "sourcecontrol"),
            new(PanelKeys.Terminal, PanelKeys.Terminal, "终端", "terminal"),
            new(PanelKeys.Experts, PanelKeys.Experts, "连接器", "connector"),
            new(PanelKeys.Automation, PanelKeys.Automation, "自动化", "automation"),
            new(PanelKeys.Activity, PanelKeys.Activity, "活动", "history"),
            new(PanelKeys.Knowledge, PanelKeys.Knowledge, "资料库", "knowledge"),
            new(PanelKeys.KnowledgeScm, PanelKeys.KnowledgeScm, "资料库·SCM", "sourcecontrol"),
            new(PanelKeys.Browser, PanelKeys.Browser, "网络", "browser"),
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天右栏", "chat"),
            new(PanelKeys.ChatPage, PanelKeys.ChatPage, "AI 聊天", "chat"),
        },
        Layout: new LayoutSplit(
            Orientation: SplitOrientation.Horizontal,
            Children: new LayoutNode[]
            {
                // 最左：侧栏 shell 级独立左栏（240px，可拖宽，documents/sourcecontrol/knowledge tab 时占满高度，叠放文件树+SCM+.md 树按 tab 切）。
                new LayoutRegion(RegionId: PanelKeys.LeftTree),
                // 中：上下分割——上 = 主面板 tabbed（终端移出 nav），下 = 终端底部常驻 region（VSCode 式底部面板方案 A）。
                new LayoutSplit(
                    Orientation: SplitOrientation.Vertical,
                    Children: new LayoutNode[]
                    {
                        new LayoutTabbed(RegionIds: new[] { PanelKeys.ChatPage, PanelKeys.Knowledge, PanelKeys.KnowledgeScm, PanelKeys.Documents, PanelKeys.SourceControl, PanelKeys.Experts, PanelKeys.Automation, PanelKeys.Activity, PanelKeys.Browser }, SelectedIndex: 0),
                        new LayoutRegion(RegionId: PanelKeys.Terminal),
                    },
                    // Pixels: [0, 220] → 主面板区 Star（弹性）+ 终端区 220px 固定（可拖高度，拖动后持久化）。
                    Pixels: new double[] { 0, 220 }),
                new LayoutRegion(RegionId: PanelKeys.Chat),
            },
            // Pixels: [240, 0, 420] → 文件树 240px 固定 + 中区 Star（弹性）+ chat 区 420px 固定。
            Pixels: new double[] { 240, 0, 420 }));
}
