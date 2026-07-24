using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acme.Quotes.Domain;

namespace Acme.Quotes.Application;

public class AnalysisManager : IAnalysisManager
{
    private readonly ISecurityLookup _securityLookup;
    private readonly IQuoteRepository _quoteRepository;
    private readonly IFailoverQuoteProvider _quoteProvider;
    private readonly IIndicatorCalculator _indicatorCalculator;

    public AnalysisManager(
        ISecurityLookup securityLookup,
        IQuoteRepository quoteRepository,
        IFailoverQuoteProvider quoteProvider,
        IIndicatorCalculator indicatorCalculator)
    {
        _securityLookup = securityLookup;
        _quoteRepository = quoteRepository;
        _quoteProvider = quoteProvider;
        _indicatorCalculator = indicatorCalculator;
    }

    public async Task<TechnicalAnalysisResult> CalculateAsync(
        string keyword,
        string freq,
        string indicators,
        int rowCount = 60)
    {
        var (quotes, security, error) = await GetKlineAsync(keyword, freq);
        if (quotes == null || security == null)
        {
            return TechnicalAnalysisResult.Failure(error ?? "无法获取行情数据");
        }

        if (quotes.Count < 5)
        {
            return TechnicalAnalysisResult.Failure($"行情数据不足（仅{quotes.Count}条），无法计算指标");
        }

        var csv = await _indicatorCalculator.CalculateAsCsvAsync(quotes, indicators, rowCount);
        return TechnicalAnalysisResult.Success(csv, quotes.Count, security);
    }

    /// <inheritdoc />
    public async Task<(List<Quote>? Quotes, SecurityRef? Security, string? Error)> GetKlineAsync(string keyword, string freq)
    {
        var securities = await _securityLookup.SearchAsync(keyword);
        if (securities.Count == 0)
        {
            return (null, null, "未找到匹配的证券");
        }

        var security = securities[0];
        var existingQuotes = await _quoteRepository.GetBySecurityIdAsync(security.Id, freq, 1000);
        List<Quote> quotes;

        // 检查是否需要从行情提供器获取数据
        if (NeedFetchFromProvider(existingQuotes))
        {
            quotes = await _quoteProvider.GetKlineAsync(security.Code, freq, security.Type, security.Exchange);
            if (quotes.Count > 0)
            {
                // 确保所有行情数据的 SecurityId 都设置为当前证券的 Id
                foreach (var quote in quotes)
                {
                    quote.SetSecurityId(security.Id);
                    quote.SetFreq(freq);
                }

                // 补全大于数据库最大日期的行情数据
                if (existingQuotes.Count > 0)
                {
                    var maxDate = existingQuotes.Max(q => q.Date);
                    var quotesToAdd = quotes.Where(q => q.Date > maxDate).ToList();
                    if (quotesToAdd.Count > 0)
                    {
                        await _quoteRepository.AddRangeAsync(quotesToAdd);
                    }
                }
                else
                {
                    // 如果数据库中没有行情数据，则新增所有行情数据
                    await _quoteRepository.AddRangeAsync(quotes);
                }

                // 永远更新数据库中的最后一个日期的数据
                var latestQuote = quotes.LastOrDefault();
                if (latestQuote != null)
                {
                    await _quoteRepository.UpdateAsync(latestQuote);
                }
            }
            else
            {
                return (null, null, $"无法获取证券 {security.Code} 的行情数据，请检查网络连接");
            }
        }
        else
        {
            // 直接从数据库获取行情数据
            quotes = existingQuotes;
        }

        return (quotes, security, null);
    }

    /// <summary>
    /// 判断是否需要从行情提供器获取数据
    /// </summary>
    private bool NeedFetchFromProvider(List<Quote> existingQuotes)
    {
        // 如果数据库没有数据，需要获取
        if (existingQuotes == null || existingQuotes.Count == 0)
        {
            return true;
        }

        var now = DateTime.Now;
        var today = now.Date;
        var morningStart = new TimeSpan(9, 30, 0);
        var afternoonEnd = new TimeSpan(15, 0, 0);

        // 检查是否为交易日
        if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
        {
            // 非交易日，不需要当日数据
            return false;
        }

        // 检查当前时间是否在开盘前
        if (now.TimeOfDay < morningStart)
        {
            // 开盘前，不需要当日数据
            return false;
        }

        // 开盘后，需要当日数据，且最后一条数据更新时间大于关盘时间
        var todayQuote = existingQuotes.FirstOrDefault(q => q.Date.Date == today);
        if (todayQuote == null)
        {
            // 没有当日数据，需要获取
            return true;
        }

        // 检查最后一条数据的更新时间是否大于关盘时间
        return todayQuote.UpdatedAt.TimeOfDay <= afternoonEnd;
    }
}
