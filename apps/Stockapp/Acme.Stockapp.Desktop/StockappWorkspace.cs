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
            new("notes", "notes", "笔记", "notes"),
            new("experts", "experts", "专家", "experts"),
            new("automation", "automation", "自动化", "automation"),
            new("ctp", "ctp", "期货", "futures"),
            new("backtest", "backtest", "回测", "backtest"),
            new("stocktrade", "stocktrade", "股票", "activity"),
            new("chat", "chat", "聊天", "chat"),
        },
        Layout: new LayoutSplit(
            Orientation: SplitOrientation.Horizontal,
            Children: new LayoutNode[]
            {
                new LayoutTabbed(RegionIds: new[] { "notes", "experts", "automation", "ctp", "backtest", "stocktrade" }, SelectedIndex: 0),
                new LayoutRegion(RegionId: "chat"),
            },
            // Pixels: [0, 420] → 内容区 Star（弹性）+ chat 区 420px 固定。
            Pixels: new double[] { 0, 420 }));
}
