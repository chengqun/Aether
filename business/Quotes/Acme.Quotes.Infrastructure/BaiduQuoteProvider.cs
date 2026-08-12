using Acme.Quotes.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Quotes.Infrastructure;

public class BaiduQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BaiduQuoteProvider> _logger;
    private const string BaseUrl = "https://finance.pae.baidu.com/vapi/v1/getquotation";

    private readonly Dictionary<string, string> _frequencyMap = new()
    {
        {"101", "day"},
    };

    public BaiduQuoteProvider(HttpClient httpClient, ILogger<BaiduQuoteProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        // 百度行情接口对无 UA 的请求直接返回 403 Forbidden。
        QuoteHttpHeaders.ApplyBrowserUserAgent(_httpClient);
    }

    public bool IsSupported(string code, string freq, string type, string exchange)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(freq) || string.IsNullOrWhiteSpace(type))
            return false;

        return _frequencyMap.ContainsKey(freq)
              &&
               (type == "汇率" || type == "指数" || type == "期货" || type == "股票"
               || type == "美股指数" || type == "虚拟币"
               );
    }

    public async Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange, CancellationToken ct = default)
    {
        if (!IsSupported(code, freq, type, exchange))
            return new List<Quote>();

        if (type == "期货")
        {
            bool hasMonthNumber = Regex.IsMatch(code, @"0[1-9]|1[0-2]");
            if (!hasMonthNumber)
            {
                code = code + "888";
            }
        }

        try
        {
            var url = BuildRequestUrl(code, freq, type, exchange);

            // 简单重试：最多3次，500ms间隔，仅对 HttpRequestException 重试
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(url, ct);
                    return ParseKlineData(response);
                }
                catch (HttpRequestException ex) when (attempt < 3)
                {
                    _logger.LogWarning(ex, "百度行情请求失败 (尝试 {Attempt}/3)，500ms 后重试: {Url}", attempt, url);
                    await Task.Delay(500, ct);
                }
            }

            // 第3次重试也失败
            _logger.LogWarning("百度行情最终失败 (3次重试后): {Url}", url);
            return new List<Quote>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "百度行情获取失败");
            return new List<Quote>();
        }
    }

    private string BuildRequestUrl(string code, string freq, string type, string exchange)
    {
        string groupParam = type switch
        {
            "汇率" => "huilv_kline",
            "虚拟币" => "huilv_kline",
            "指数" => "quotation_index_kline",
            "美股指数" => "quotation_index_kline",
            "期货" => "quotation_futures_kline",
            "股票" => "quotation_kline_ab",
            _ => "quotation_kline_ab"
        };

        string ktype = _frequencyMap.TryGetValue(freq, out var val) ? val : "day";

        // market_type 按品种区分：A 股=ab（旧代码硬编码 "us" 致 403/空——美股市场类型取不到 A 股数据）。
        string marketType = MarketTypeFor(type);
        return $"{BaseUrl}?group={groupParam}&ktype={ktype}&code={code}&market_type={marketType}";
    }

    private static string MarketTypeFor(string type) => type switch
    {
        "股票" => "ab",
        "期货" => "futures",
        "指数" => "index",
        "美股指数" => "us",
        "汇率" => "huilv",
        "虚拟币" => "huilv",
        _ => "ab",
    };

    private List<Quote> ParseKlineData(string response)
    {
        var quotes = new List<Quote>();
        try
        {
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("Result", out var resultElement) ||
                !resultElement.TryGetProperty("newMarketData", out var newMarketDataElement) ||
                !newMarketDataElement.TryGetProperty("keys", out var keysElement) ||
                !newMarketDataElement.TryGetProperty("marketData", out var marketDataElement))
            {
                return new List<Quote>();
            }

            var keys = keysElement.EnumerateArray()
                .Select(k => k.GetString())
                .ToList();

            int openIndex = keys.IndexOf("open");
            int closeIndex = keys.IndexOf("close");
            int highIndex = keys.IndexOf("high");
            int lowIndex = keys.IndexOf("low");
            int timeIndex = keys.IndexOf("time");
            int volumeIndex = keys.IndexOf("volume");

            if (openIndex == -1 || closeIndex == -1 || highIndex == -1 || lowIndex == -1 || timeIndex == -1)
            {
                return new List<Quote>();
            }

            var marketDataStr = marketDataElement.GetString();
            if (string.IsNullOrEmpty(marketDataStr))
            {
                return new List<Quote>();
            }

            foreach (var item in marketDataStr.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(item)) continue;

                var parts = item.Split(',');
                int maxIndex = new[] { openIndex, closeIndex, highIndex, lowIndex, timeIndex, volumeIndex }.Max();
                if (parts.Length <= maxIndex) continue;

                if (!DateTime.TryParse($"{parts[timeIndex]} 15:00", out DateTime date)) continue;

                quotes.Add(new Quote(
                    0,
                    date,
                    decimal.TryParse(parts[openIndex], out var open) ? open : 0,
                    decimal.TryParse(parts[highIndex], out var high) ? high : 0,
                    decimal.TryParse(parts[lowIndex], out var low) ? low : 0,
                    decimal.TryParse(parts[closeIndex], out var close) ? close : 0,
                    volumeIndex != -1 && decimal.TryParse(parts[volumeIndex], out var volume) ? volume : 0
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "百度K线数据解析失败");
        }

        return quotes;
    }
}
