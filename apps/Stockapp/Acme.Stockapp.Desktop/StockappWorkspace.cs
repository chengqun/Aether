using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Stockapp.Desktop;

/// <summary>
/// Stockapp 租户工作台定义：左 = 6 panel tabbed（笔记/专家/自动化/期货/回测/股票），右 = chat region。
/// nav 6 项数据驱动自 LayoutTabbed——比 Qifu 多 CTP 期货 + 回测 + 股票（外部版股票期货场景）。
/// icon 用 icons.json manifest key（notes/experts/automation/futures/backtest/activity/chat）。
/// </summary>
public static class StockappWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "stockapp",
        Title: "Stockapp 工作台",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.Documents, PanelKeys.Documents, "文档", "documents"),
            new(PanelKeys.SourceControl, PanelKeys.SourceControl, "源代码管理", "sourcecontrol"),
            new(PanelKeys.Notes, PanelKeys.Notes, "笔记", "notes"),
            new(PanelKeys.Experts, PanelKeys.Experts, "专家", "experts"),
            new(PanelKeys.Automation, PanelKeys.Automation, "自动化", "automation"),
            new(PanelKeys.Ctp, PanelKeys.Ctp, "期货", "futures"),
            new(PanelKeys.Backtest, PanelKeys.Backtest, "回测", "backtest"),
            new(PanelKeys.StockTrade, PanelKeys.StockTrade, "股票", "activity"),
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天", "chat"),
        },
        Layout: new LayoutSplit(
            Orientation: SplitOrientation.Horizontal,
            Children: new LayoutNode[]
            {
                new LayoutTabbed(RegionIds: new[] { PanelKeys.Documents, PanelKeys.SourceControl, PanelKeys.Notes, PanelKeys.Experts, PanelKeys.Automation, PanelKeys.Ctp, PanelKeys.Backtest, PanelKeys.StockTrade }, SelectedIndex: 0),
                new LayoutRegion(RegionId: PanelKeys.Chat),
            },
            // Pixels: [0, 420] → 内容区 Star（弹性）+ chat 区 420px 固定。
            Pixels: new double[] { 0, 420 }));
}
