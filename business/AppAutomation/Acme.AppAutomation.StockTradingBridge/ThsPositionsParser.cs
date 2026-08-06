using System.Globalization;
using Acme.StockTrading.Domain;

namespace Acme.AppAutomation.StockTradingBridge;

/// <summary>
/// 解析同花顺持仓表文本（Tab 分隔，首行表头）。列序移植自已验证的参考实现 <c>FlaUiStockBrokerage.ParsePositions</c>：
/// 0 代码 / 1 名称 / 2 余额 / 3 可用 / 4 冻结 / 5 成本价 / 6 市价 / 7 盈亏 / 8 盈亏比 / 9 市值 / 10 持股天数 / 11 市场。
/// <para>FlaUI 路径经剪贴板（Ctrl+A/Ctrl+C）取此文本；MCP 路径经 desktop server 的 <c>get_text</c>/<c>get_ui_tree</c>
/// 取面板文本——两端拿到的是同构 Tab 文本，共用此解析。</para>
/// </summary>
public static class ThsPositionsParser
{
    /// <summary>解析 Tab 分隔的持仓文本（支持 \r\n / \n 行分隔）。容错：行字段不足 12 列或解析失败则跳过该行。</summary>
    public static IReadOnlyList<StockPosition> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StockPosition>();

        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return Array.Empty<StockPosition>();

        var list = new List<StockPosition>();
        for (int i = 1; i < lines.Length; i++) // 跳过表头
        {
            try
            {
                var fields = lines[i].Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 12) continue;

                list.Add(new StockPosition
                {
                    Symbol = fields[0],
                    Name = fields[1],
                    Quantity = int.TryParse(fields[2], out int stockBalance) ? stockBalance : 0,
                    AvailableQuantity = int.TryParse(fields[3], out int availableBalance) ? availableBalance : 0,
                    FrozenQuantity = int.TryParse(fields[4], out int frozenQuantity) ? frozenQuantity : 0,
                    CostPrice = decimal.TryParse(fields[5], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal costPrice) ? costPrice : 0,
                    CurrentPrice = decimal.TryParse(fields[6], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal marketPrice) ? marketPrice : 0,
                    ProfitLoss = decimal.TryParse(fields[7], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal profitLoss) ? profitLoss : 0,
                    ProfitLossRatio = decimal.TryParse(fields[8], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal profitLossRatio) ? profitLossRatio : 0,
                    MarketValue = decimal.TryParse(fields[9], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal marketValue) ? marketValue : 0,
                    HoldingDays = int.TryParse(fields[10], out int holdingDays) ? holdingDays : 0,
                    ExchangeMarket = fields[11],
                });
            }
            catch { continue; }
        }
        return list;
    }
}
