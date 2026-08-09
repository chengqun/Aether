namespace Aether.Server.Application.Contracts.Services.Security;

/// <summary>
/// 同步选股策略发朋友圈的结果
/// </summary>
public class StockPickingMomentResult
{
    /// <summary>
    /// 策略总数
    /// </summary>
    public int TotalStrategies { get; set; }

    /// <summary>
    /// 成功发布的朋友圈数
    /// </summary>
    public int PublishedMoments { get; set; }

    /// <summary>
    /// 未匹配到的股票数（code 在 AppSecurities 中找不到）
    /// </summary>
    public int SkippedStocks { get; set; }
}
