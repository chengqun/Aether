using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Stockapp.Desktop;

/// <summary>
/// Stockapp 租户工作台定义：外层 = 水平 3 分割（侧栏 | 上下分割[主面板 tabbed + 终端] | chat）。
/// 侧栏是 shell 级独立左栏（VSCode 式：占满高度，终端只压编辑器区不压树）；是 LayoutRegion 不进 LayoutTabbed → 不进 nav rail，
/// 叠放文件树 + SCM 面板 + .md 树，documents/sourcecontrol/knowledge tab 选中时可见并切对应树（WorkspaceView 按 SelectedTabIndex 切列宽 0/恢复 + SetActiveTree）。
/// nav 项数据驱动自 LayoutTabbed——只放「目的地」页（VSCode 式三栏心智：nav=目的地、右栏=聊天助手、底部=过程遥测）。
/// 终端在底部 region 不进 nav（VSCode 式底部面板：agent run_in_terminal 时
/// 终端与上方主面板同时可见，不用切 tab）；聊天住右栏 ChatView、网络住底部「网络」tab，均无 nav 全屏页。
/// 比 Qifu 多 CTP 期货 + 回测 + 股票（外部版股票期货场景）。
/// icon 用 codicon key（connector/automation/futures/backtest/activity/chat）。
/// Title 是 nav rail 常显标签——保持 2-4 字（源码管理/资料版本——资料库+其版本管理成对，全汉字 ≤4 字）；Group 连续变化时 rail 项间加间距分组（创作/运行/洞察/业务）。
/// </summary>
public static class StockappWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "stockapp",
        Title: "Stockapp 工作台",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.LeftTree, PanelKeys.LeftTree, "侧栏", "files"),
            new(PanelKeys.Documents, PanelKeys.Documents, "文档", "documents", "创作"),
            new(PanelKeys.SourceControl, PanelKeys.SourceControl, "源码管理", "sourcecontrol", "创作"),
            new(PanelKeys.Terminal, PanelKeys.Terminal, "终端", "terminal"),
            new(PanelKeys.Experts, PanelKeys.Experts, "连接器", "connector", "运行"),
            new(PanelKeys.Automation, PanelKeys.Automation, "自动化", "automation", "运行"),
            new(PanelKeys.Activity, PanelKeys.Activity, "活动", "history", "洞察"),
            new(PanelKeys.Knowledge, PanelKeys.Knowledge, "资料库", "knowledge", "创作"),
            new(PanelKeys.KnowledgeScm, PanelKeys.KnowledgeScm, "资料版本", "sourcecontrol", "创作"),
            new(PanelKeys.Ctp, PanelKeys.Ctp, "期货", "futures", "业务"),
            new(PanelKeys.Backtest, PanelKeys.Backtest, "回测", "backtest", "业务"),
            new(PanelKeys.StockTrade, PanelKeys.StockTrade, "股票", "activity", "业务"),
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天右栏", "chat"),
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
                        new LayoutTabbed(RegionIds: new[] { PanelKeys.Knowledge, PanelKeys.KnowledgeScm, PanelKeys.Documents, PanelKeys.SourceControl, PanelKeys.Experts, PanelKeys.Automation, PanelKeys.Activity, PanelKeys.Ctp, PanelKeys.Backtest, PanelKeys.StockTrade }, SelectedIndex: 0),
                        new LayoutRegion(RegionId: PanelKeys.Terminal),
                    },
                    // Pixels: [0, 220] → 主面板区 Star（弹性）+ 终端区 220px 固定（可拖高度，拖动后持久化）。
                    Pixels: new double[] { 0, 220 }),
                new LayoutRegion(RegionId: PanelKeys.Chat),
            },
            // Pixels: [240, 0, 420] → 文件树 240px 固定 + 中区 Star（弹性）+ chat 区 420px 固定。
            Pixels: new double[] { 240, 0, 420 }));
}
