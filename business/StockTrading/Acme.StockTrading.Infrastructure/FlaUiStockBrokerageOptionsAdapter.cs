using Acme.StockTrading.Domain;

namespace Acme.StockTrading.Infrastructure;

/// <summary>
/// 把 <see cref="IStockTradingOptions"/> 桥接到 <see cref="FlaUiStockBrokerageOptions"/> 单例。
/// 改这里的字段即时生效——<c>FlaUiStockBrokerage</c> 持有同一个 options 引用，每次操作现读。
/// </summary>
public sealed class FlaUiStockBrokerageOptionsAdapter : IStockTradingOptions
{
    private readonly FlaUiStockBrokerageOptions _options;

    public FlaUiStockBrokerageOptionsAdapter(FlaUiStockBrokerageOptions options)
    {
        _options = options;
    }

    public string ClientExecutablePath
    {
        get => _options.ClientExecutablePath ?? "";
        set => _options.ClientExecutablePath = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public string ClientWindowTitle
    {
        get => _options.ClientWindowTitle;
        set => _options.ClientWindowTitle = string.IsNullOrWhiteSpace(value)
            ? "网上股票交易系统5.0"
            : value;
    }

    public int StepDelayMs
    {
        get => _options.StepDelayMs;
        set => _options.StepDelayMs = value < 0 ? 0 : value;
    }
}
