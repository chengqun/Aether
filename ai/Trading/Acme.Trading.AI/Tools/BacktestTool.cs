using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Application;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 回测工具（接入 Agent 工具池）。让动态 Agent 能触发一次 QuantConnect LEAN 回测并拿到统计指标。
/// 策略走共享 StrategyRegistry（如 SmaCross），数据统一走 IAnalysisManager（cache-first 取期货历史 K 线）。
/// 依赖 <see cref="IBacktestApplicationService"/>。
/// </summary>
public class BacktestTool
{
    private readonly IBacktestApplicationService _service;

    public BacktestTool(IBacktestApplicationService service)
    {
        _service = service;
    }

    /// <summary>运行回测返回结果</summary>
    public class RunOutput
    {
        public bool IsSuccess { get; set; }
        public string? AlgorithmStatus { get; set; }
        public string? Error { get; set; }
        public BacktestStatistics? Statistics { get; set; }
        public int EquityPointCount { get; set; }
    }

    /// <summary>
    /// 用 SMA 双线交叉策略跑一次历史回测，返回收益/风险统计。
    /// </summary>
    /// <param name="symbol">期货合约代码（如 ag2610 / rb2610 / IF2607），走项目行情 provider 取历史K线</param>
    /// <param name="startDate">回测开始日期（yyyy-MM-dd）</param>
    /// <param name="endDate">回测结束日期（yyyy-MM-dd）</param>
    /// <param name="strategy">策略名（StrategyRegistry 注册的 key，如 SmaCross），默认 SmaCross</param>
    /// <param name="initialCash">初始资金，默认 100000</param>
    /// <param name="fastPeriod">短期均线周期，默认 10</param>
    /// <param name="slowPeriod">长期均线周期，默认 30</param>
    /// <param name="resolution">K 线分辨率（Daily/Hour/Minute），默认 Daily</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<RunOutput> RunBacktest(
        [Description("期货合约代码（如 ag2610 / rb2610 / IF2607）")] string symbol,
        [Description("回测开始日期 yyyy-MM-dd")] string startDate,
        [Description("回测结束日期 yyyy-MM-dd")] string endDate,
        [Description("策略名（StrategyRegistry 注册的 key，如 SmaCross），默认 SmaCross")] string strategy = "SmaCross",
        [Description("初始资金，默认 100000")] decimal initialCash = 100_000m,
        [Description("短期均线周期，默认 10")] int fastPeriod = 10,
        [Description("长期均线周期，默认 30")] int slowPeriod = 30,
        [Description("K 线分辨率：Daily/Hour/Minute，默认 Daily")] string resolution = "Daily",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!DateTime.TryParse(startDate, out var start))
                return new RunOutput { Error = $"开始日期格式无效: {startDate}" };
            if (!DateTime.TryParse(endDate, out var end))
                return new RunOutput { Error = $"结束日期格式无效: {endDate}" };
            if (fastPeriod >= slowPeriod)
                return new RunOutput { Error = $"短期周期({fastPeriod})必须小于长期周期({slowPeriod})" };

            if (!Enum.TryParse<BarResolution>(resolution, ignoreCase: true, out var barRes))
                barRes = BarResolution.Daily;

            var input = new BacktestRunInput
            {
                StrategyName = strategy,
                Symbol = symbol,
                StartDate = start,
                EndDate = end,
                InitialCash = initialCash,
                FastPeriod = fastPeriod,
                SlowPeriod = slowPeriod,
                Resolution = barRes,
            };

            var result = await _service.RunAsync(input, cancellationToken);
            return new RunOutput
            {
                IsSuccess = result.IsSuccess,
                AlgorithmStatus = result.AlgorithmStatus,
                Error = result.Error,
                Statistics = result.Statistics,
                EquityPointCount = result.EquityCurve.Count,
            };
        }
        catch (System.Exception ex)
        {
            return new RunOutput { Error = ex.Message };
        }
    }
}
