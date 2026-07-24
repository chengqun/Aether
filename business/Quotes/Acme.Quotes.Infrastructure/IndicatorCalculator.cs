using System.Text;
using System.Text.RegularExpressions;
using Acme.Quotes.Domain;
using Quote = Acme.Quotes.Domain.Quote;
using Skender.Stock.Indicators;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;

namespace Acme.Quotes.Infrastructure;

public sealed class IndicatorCalculator : IIndicatorCalculator
{
    public Task<string> CalculateAsCsvAsync(List<Quote> quotes, string indicators, int rowCount = 60)
    {
        if (string.IsNullOrWhiteSpace(indicators))
            throw new ArgumentException("指标名称不能为空", nameof(indicators));

        var indicatorNameList = indicators.Split(',')
            .Select(s => s.Trim().ToUpper())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (!indicatorNameList.Any())
            throw new ArgumentException("没有有效的指标名称", nameof(indicators));

        var indicatorQuotes = ConvertToIndicatorsQuotes(quotes);
        var dateToIndicatorsMap = new Dictionary<DateTime, Dictionary<string, object?>>();
        var dateToQuoteMap = quotes.ToDictionary(q => q.Date, q => q);

        foreach (var indicatorName in indicatorNameList)
        {
            var (indicatorType, parameters) = ParseIndicatorNameWithParameters(indicatorName);
            var results = CalculateSingleIndicator(indicatorQuotes, indicatorType, parameters);

            foreach (var result in results)
            {
                if (result.TryGetValue("Date", out var dateObj) && dateObj is DateTime date)
                {
                    if (!dateToIndicatorsMap.ContainsKey(date))
                    {
                        var row = new Dictionary<string, object?> { { "Date", date } };
                        if (dateToQuoteMap.TryGetValue(date, out var quote))
                        {
                            row["Open"] = quote.Open;
                            row["High"] = quote.High;
                            row["Low"] = quote.Low;
                            row["Close"] = quote.Close;
                            row["Volume"] = quote.Volume;
                        }
                        dateToIndicatorsMap[date] = row;
                    }

                    foreach (var kvp in result)
                    {
                        if (kvp.Key != "Date")
                            dateToIndicatorsMap[date][kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        var orderedResults = dateToIndicatorsMap.Values
            .OrderBy(r => r["Date"] as DateTime?)
            .ToList();

        if (rowCount > 0 && orderedResults.Count > rowCount)
            orderedResults = orderedResults.TakeLast(rowCount).ToList();

        return Task.FromResult(GenerateCsv(orderedResults));
    }

    private List<IQuote> ConvertToIndicatorsQuotes(List<Quote> quotes)
    {
        return quotes.Select(q => (IQuote)new QuoteAdapter
        {
            Date = q.Date,
            Open = q.Open,
            High = q.High,
            Low = q.Low,
            Close = q.Close,
            Volume = q.Volume
        }).ToList();
    }

    private enum IndicatorType
    {
        SMA, EMA, RSI, MACD, BB, SAR, KDJ, ADX
    }

    private List<Dictionary<string, object>> CalculateSingleIndicator(
        List<IQuote> quotes,
        IndicatorType indicatorType,
        Dictionary<string, object> parameters)
    {
        return indicatorType switch
        {
            IndicatorType.SMA => CalculateSma(quotes, parameters),
            IndicatorType.EMA => CalculateEma(quotes, parameters),
            IndicatorType.RSI => CalculateRsi(quotes, parameters),
            IndicatorType.MACD => CalculateMacd(quotes, parameters),
            IndicatorType.BB => CalculateBollingerBands(quotes, parameters),
            IndicatorType.SAR => CalculateSar(quotes, parameters),
            IndicatorType.KDJ => CalculateKdj(quotes, parameters),
            IndicatorType.ADX => CalculateAdx(quotes, parameters),
            _ => throw new NotSupportedException($"不支持的指标类型: {indicatorType}")
        };
    }

    private List<Dictionary<string, object>> CalculateSma(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 20;
        var results = quotes.GetSma(period);

        return results.Where(r => r.Sma != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"SMA({period})", r.Sma!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateEma(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 20;
        var results = quotes.GetEma(period);

        return results.Where(r => r.Ema != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"EMA({period})", r.Ema!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateRsi(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 14;
        var results = quotes.GetRsi(period);

        return results.Where(r => r.Rsi != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"RSI({period})", r.Rsi!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateMacd(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int fastPeriod = parameters.TryGetValue("FastPeriod", out var fastObj) ? Convert.ToInt32(fastObj) : 12;
        int slowPeriod = parameters.TryGetValue("SlowPeriod", out var slowObj) ? Convert.ToInt32(slowObj) : 26;
        int signalPeriod = parameters.TryGetValue("SignalPeriod", out var signalObj) ? Convert.ToInt32(signalObj) : 9;
        var results = quotes.GetMacd(fastPeriod, slowPeriod, signalPeriod);

        return results.Where(r => r.Macd != null && r.Signal != null && r.Histogram != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"MACD({fastPeriod},{slowPeriod},{signalPeriod})", r.Macd!.Value },
                { $"MACD_Signal({signalPeriod})", r.Signal!.Value },
                { "MACD_Histogram", r.Histogram!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateBollingerBands(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 20;
        double stdDeviations = parameters.TryGetValue("StandardDeviations", out var stdObj) ? Convert.ToDouble(stdObj) : 2.0;
        var results = quotes.GetBollingerBands(period, stdDeviations);

        return results.Where(r => r.Sma != null && r.UpperBand != null && r.LowerBand != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"BB_Upper({period},{stdDeviations})", r.UpperBand!.Value },
                { $"BB_Middle({period})", r.Sma!.Value },
                { $"BB_Lower({period},{stdDeviations})", r.LowerBand!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateSar(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        double af = parameters.TryGetValue("AccelerationFactor", out var afObj) ? Convert.ToDouble(afObj) : 0.02;
        double afMax = parameters.TryGetValue("AccelerationFactorMax", out var afmObj) ? Convert.ToDouble(afmObj) : 0.2;
        var results = quotes.GetParabolicSar(af, afMax);

        return results.Where(r => r.Sar != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"SAR({af},{afMax})", r.Sar!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateKdj(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 9;
        int signalPeriod = parameters.TryGetValue("SignalPeriod", out var signalObj) ? Convert.ToInt32(signalObj) : 3;
        int smoothPeriod = parameters.TryGetValue("SmoothPeriod", out var smoothObj) ? Convert.ToInt32(smoothObj) : 3;
        var results = quotes.GetStoch(period, signalPeriod, smoothPeriod);

        return results.Where(r => r.K != null && r.D != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"KDJ_K({period},{signalPeriod},{smoothPeriod})", r.K!.Value },
                { $"KDJ_D({signalPeriod},{smoothPeriod})", r.D!.Value },
                { $"KDJ_J({period},{signalPeriod},{smoothPeriod})", 3 * r.K!.Value - 2 * r.D!.Value }
            })
            .ToList();
    }

    private List<Dictionary<string, object>> CalculateAdx(List<IQuote> quotes, Dictionary<string, object> parameters)
    {
        int period = parameters.TryGetValue("Period", out var periodObj) ? Convert.ToInt32(periodObj) : 14;
        var results = quotes.GetAdx(period);

        return results.Where(r => r.Adx != null)
            .Select(r => new Dictionary<string, object>
            {
                { "Date", r.Date },
                { $"ADX({period})", r.Adx!.Value }
            })
            .ToList();
    }

    private (IndicatorType IndicatorType, Dictionary<string, object> Parameters) ParseIndicatorNameWithParameters(string indicatorName)
    {
        var bracketMatch = Regex.Match(indicatorName, @"^([A-Z]+)\(([^)]+)\)$");

        if (bracketMatch.Success)
        {
            string typePart = bracketMatch.Groups[1].Value;
            string paramsPart = bracketMatch.Groups[2].Value;

            if (Enum.TryParse<IndicatorType>(typePart, true, out var indicatorType))
            {
                var paramValues = paramsPart.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                var paramConfig = new Dictionary<IndicatorType, List<string>>
                {
                    { IndicatorType.SMA, new List<string> { "Period" } },
                    { IndicatorType.EMA, new List<string> { "Period" } },
                    { IndicatorType.RSI, new List<string> { "Period" } },
                    { IndicatorType.ADX, new List<string> { "Period" } },
                    { IndicatorType.MACD, new List<string> { "FastPeriod", "SlowPeriod", "SignalPeriod" } },
                    { IndicatorType.KDJ, new List<string> { "Period", "SlowingPeriod", "SignalPeriod" } },
                    { IndicatorType.BB, new List<string> { "Period", "StandardDeviations" } },
                    { IndicatorType.SAR, new List<string> { "AccelerationFactor", "AccelerationFactorMax" } }
                };

                if (paramConfig.TryGetValue(indicatorType, out var paramNames))
                {
                    var parameters = new Dictionary<string, object>();
                    for (int i = 0; i < Math.Min(paramValues.Count, paramNames.Count); i++)
                    {
                        string paramName = paramNames[i];
                        string paramValue = paramValues[i];

                        if (indicatorType == IndicatorType.SAR ||
                            (indicatorType == IndicatorType.BB && paramName == "StandardDeviations"))
                        {
                            if (double.TryParse(paramValue, out var doubleValue))
                                parameters[paramName] = doubleValue;
                        }
                        else
                        {
                            if (int.TryParse(paramValue, out var intValue))
                                parameters[paramName] = intValue;
                        }
                    }
                    return (indicatorType, parameters);
                }
                return (indicatorType, new Dictionary<string, object>());
            }
        }

        var numberMatch = Regex.Match(indicatorName, @"^([A-Z]+)(\d+)$");
        if (numberMatch.Success)
        {
            string typePart = numberMatch.Groups[1].Value;
            int period = int.Parse(numberMatch.Groups[2].Value);

            if (Enum.TryParse<IndicatorType>(typePart, true, out var indicatorType))
                return (indicatorType, new Dictionary<string, object> { { "Period", period } });
        }

        if (Enum.TryParse<IndicatorType>(indicatorName, true, out var simpleType))
            return (simpleType, new Dictionary<string, object>());

        throw new ArgumentException($"无效的指标名称: {indicatorName}", nameof(indicatorName));
    }

    private string GenerateCsv(List<Dictionary<string, object?>> results)
    {
        if (results.Count == 0)
            return string.Empty;

        var allColumns = new HashSet<string> { "Date", "Open", "High", "Low", "Close", "Volume" };
        foreach (var result in results)
            allColumns.UnionWith(result.Keys);

        var orderedColumns = new List<string> { "Date", "Open", "High", "Low", "Close", "Volume" };
        orderedColumns.AddRange(allColumns.Where(c => !orderedColumns.Contains(c)));

        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine(string.Join(",", orderedColumns));

        foreach (var row in results)
        {
            var values = orderedColumns.Select(col =>
            {
                if (row.TryGetValue(col, out var value))
                    return FormatValue(value, col);
                return string.Empty;
            });
            csvBuilder.AppendLine(string.Join(",", values));
        }

        return csvBuilder.ToString();
    }

    private string FormatValue(object? value, string columnName)
    {
        if (value == null) return string.Empty;

        if (value is DateTime dateValue)
            return dateValue.ToString("yyyy-MM-dd HH:mm:ss");

        if (value is decimal decValue || value is double dblValue || value is float fltValue)
        {
            var colLower = columnName.ToLower();
            if (colLower.Contains("volume"))
                return string.Format("{0:F0}", value);
            if (colLower.Contains("open") || colLower.Contains("high") || colLower.Contains("low") || colLower.Contains("close"))
                return string.Format("{0:F2}", value);
            return string.Format("{0:F4}", value);
        }

        return value.ToString() ?? string.Empty;
    }

    private class QuoteAdapter : IQuote
    {
        public DateTime Date { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }
}
