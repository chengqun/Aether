namespace Acme.StockTrading.Domain;

public enum StockDirection { Buy = 0, Sell = 1 }

/// <summary>下单请求（机制无关）。对齐参考项目 AiTrade.Core.OrderDto：StockCode/Price/Quantity/OrderType。</summary>
public sealed class StockOrderRequest
{
    public string Symbol { get; set; } = string.Empty;   // 股票代码
    public string? Name { get; set; }                    // 名称（可选，仅日志）
    public StockDirection Direction { get; set; }
    public decimal LimitPrice { get; set; }
    public int Quantity { get; set; }
}

/// <summary>持仓。对齐参考项目 AiTrade.Core.Models.Position 的字段语义。</summary>
public sealed class StockPosition
{
    public string? Symbol { get; set; }            // 证券代码
    public string? Name { get; set; }              // 证券名称
    public int Quantity { get; set; }              // 股票余额
    public int AvailableQuantity { get; set; }     // 可用余额
    public int FrozenQuantity { get; set; }        // 冻结数量
    public decimal CostPrice { get; set; }         // 成本价
    public decimal CurrentPrice { get; set; }      // 市价
    public decimal ProfitLoss { get; set; }        // 盈亏
    public decimal ProfitLossRatio { get; set; }   // 盈亏比例(%)
    public decimal MarketValue { get; set; }       // 市值
    public int HoldingDays { get; set; }           // 持股天数
    public string? ExchangeMarket { get; set; }    // 市场代码
}
