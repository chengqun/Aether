using Acme.Quotes.Application;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Quotes.AI.Tools;

public class TechnicalAnalysisTool
{
    public class Output
    {
        public string Result { get; set; } = string.Empty;
    }

    private readonly IAnalysisService _analysisService;

    public TechnicalAnalysisTool(IAnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    [Description("计算股票的技术指标，包括SMA、EMA、RSI、MACD、BB、SAR、KDJ、ADX等。当用户要求分析技术指标时调用此工具。")]
    public async Task<Output> CalculateIndicators([Description("股票代码或名称")] string keyword, [Description("K线周期代码: 101=日K, 1=1分钟, 5=5分钟, 15=15分钟, 30=30分钟, 60=60分钟")] string freq = "101", [Description("要计算的指标，用逗号分隔: SMA, EMA, RSI, MACD, BB, SAR, KDJ, ADX")] string indicators = "SMA, EMA, RSI, MACD, BB, SAR, KDJ, ADX", [Description("获取的数据行数, 默认60")] int rowCount = 60, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _analysisService.CalculateAsync(keyword, freq, indicators, rowCount, cancellationToken);

            return new Output
            {
                Result = result.IsSuccess
                    ? result.CsvData ?? "计算成功但无数据"
                    : $"计算失败: {result.ErrorMessage ?? "未知错误"}"
            };
        }
        catch (System.Exception ex)
        {
            return new Output { Result = $"技术分析失败: {ex.Message}" };
        }
    }
}
