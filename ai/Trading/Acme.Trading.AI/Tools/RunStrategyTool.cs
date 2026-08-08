using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Application;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 策略信号探针工具：进程内逐根 <see cref="IStrategy.EvaluateSignal"/> 求最近若干根 bar 的信号，返回信号摘要。
/// <b>不起 Lean 引擎、不算 PnL</b>——只回答「策略出什么信号」。与 <see cref="BacktestTool"/>（起整套 Lean 做回测、回答「表现如何」）平行。
/// 镜像规则引擎的 <c>run_rule</c>：LLM 调一次，内部跑逐根循环，返回压缩摘要，免起重引擎。
/// 依赖 <see cref="IStrategySignalApplicationService"/>。
/// </summary>
public class RunStrategyTool
{
    private readonly IStrategySignalApplicationService _service;

    public RunStrategyTool(IStrategySignalApplicationService service)
    {
        _service = service;
    }

    /// <summary>探针返回（紧凑摘要，给 LLM 看；不含逐根明细，省 token）。</summary>
    public class RunOutput
    {
        public bool IsSuccess { get; set; }
        public string? Error { get; set; }
        public string? StrategyName { get; set; }
        public string? Symbol { get; set; }
        /// <summary>最后一根 bar 的信号（= 策略当前会说啥）：Hold/Long/Flat。</summary>
        public string? LastSignal { get; set; }
        public int EvaluatedBars { get; set; }
        public int WarmUpBars { get; set; }
        public int LongCount { get; set; }
        public int FlatCount { get; set; }
        public int HoldCount { get; set; }
        public string? LastBarDate { get; set; }
        /// <summary>信号翻转点（信号变化当根的 日期+信号），按时间升序。紧凑回答「何时翻多/翻空」。</summary>
        public List<SignalChangeDto> Changes { get; set; } = new();
    }

    public class SignalChangeDto
    {
        public string? Date { get; set; }
        public string? Signal { get; set; }
    }

    /// <summary>
    /// 取某标的最近若干根 bar，进程内直接求策略信号（不起 Lean、不算 PnL）。
    /// 用于「想知道策略当前/近期出什么信号」的轻量分析；要 PnL/收益曲线请用 RunBacktest。
    /// </summary>
    /// <param name="strategy">策略名（StrategyRegistry 注册的 key，如 SmaCross）</param>
    /// <param name="symbol">标的代码（期货合约如 ag2610 / 股票如 600000）</param>
    /// <param name="barCount">取最近多少根 bar 求值（含预热期），默认 100</param>
    /// <param name="resolution">K 线分辨率：Daily/Hour/Minute，默认 Daily</param>
    /// <param name="parameters">策略参数 JSON（可选，如 {"FastPeriod":"5","SlowPeriod":"20"}）；不传用策略默认值</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<RunOutput> RunStrategy(
        [Description("策略名（StrategyRegistry 注册的 key，如 SmaCross）")] string strategy,
        [Description("标的代码（期货合约如 ag2610 / 股票如 600000）")] string symbol,
        [Description("取最近多少根 bar 求值（含预热期），默认 100")] int barCount = 100,
        [Description("K 线分辨率：Daily/Hour/Minute，默认 Daily")] string resolution = "Daily",
        [Description("策略参数 JSON（可选，如 {\"FastPeriod\":\"5\",\"SlowPeriod\":\"20\"}）；不传用策略默认值")] string? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.TryParse<BarResolution>(resolution, ignoreCase: true, out var barRes))
                barRes = BarResolution.Daily;

            Dictionary<string, string>? paramMap = null;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                try
                {
                    paramMap = JsonSerializer.Deserialize<Dictionary<string, string>>(parameters,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return new RunOutput { Error = $"参数 JSON 解析失败: {parameters}" };
                }
            }

            var input = new StrategySignalProbeInput
            {
                StrategyName = strategy,
                Symbol = symbol,
                BarCount = barCount,
                Resolution = barRes,
                StrategyParameters = paramMap,
            };

            var r = await _service.ProbeAsync(input, cancellationToken);

            if (!r.IsSuccess)
                return new RunOutput { IsSuccess = false, Error = r.Error, StrategyName = r.StrategyName, Symbol = r.Symbol };

            var output = new RunOutput
            {
                IsSuccess = true,
                StrategyName = r.StrategyName,
                Symbol = r.Symbol,
                LastSignal = r.LastSignal.ToString(),
                EvaluatedBars = r.EvaluatedBars,
                WarmUpBars = r.WarmUpBars,
                LongCount = r.LongCount,
                FlatCount = r.FlatCount,
                HoldCount = r.HoldCount,
                LastBarDate = r.LastBarDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            foreach (var c in r.Changes)
                output.Changes.Add(new SignalChangeDto
                {
                    Date = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Signal = c.Signal.ToString(),
                });
            return output;
        }
        catch (Exception ex)
        {
            return new RunOutput { Error = ex.Message };
        }
    }
}
