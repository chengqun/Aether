namespace Acme.StockTrading.Infrastructure;

/// <summary>
/// FlaUI 适配器配置（连接参数在此，不进 Domain 端口）。由宿主 DI 注册时提供。
/// 默认值对齐参考项目 AiTrade：窗口标题「网上股票交易系统5.0」，xiadan.exe 路径按实际填。
/// </summary>
public sealed class FlaUiStockBrokerageOptions
{
    /// <summary>同花顺下单客户端窗口标题（FindFirstChild ByName 精确匹配）。</summary>
    public string ClientWindowTitle { get; set; } = "网上股票交易系统5.0";

    /// <summary>未找到窗口时自动启动的 exe 路径（可选）。留空 = 仅 Attach 已运行窗口。</summary>
    public string? ClientExecutablePath { get; set; }

    /// <summary>操作间停顿（毫秒），给客户端 UI 刷新时间。</summary>
    public int StepDelayMs { get; set; } = 100;
}
