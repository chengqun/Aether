using Acme.Quotes.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Quotes.Infrastructure;

public class EastQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EastQuoteProvider> _logger;

    public EastQuoteProvider(HttpClient httpClient, ILogger<EastQuoteProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    public bool IsSupported(string code, string freq, string type, string exchange)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(freq) || string.IsNullOrWhiteSpace(type))
            return false;

        return (type == "股票"
           || (type == "期货" && freq == "101")
           || (type == "指数")
            );
    }

    public async Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange, CancellationToken ct = default)
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
                _logger.LogWarning(ex, "东方财富行情请求失败 (尝试 {Attempt}/3)，500ms 后重试: {Url}", attempt, url);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "东方财富行情获取失败: {Url}", url);
                return new List<Quote>();
            }
        }

        // 第3次重试也失败
        _logger.LogWarning("东方财富行情最终失败 (3次重试后): {Url}", url);
        return new List<Quote>();
    }

    private string BuildRequestUrl(string code, string freq, string type, string exchange)
    {
        var endDate = DateTime.Now.ToString("yyyyMMdd");
        var pureCode = code.Contains('.') ? code.Split('.')[0] : code;
        var secid = exchange + "." + pureCode;
        return $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&fields1=f1%2Cf2%2Cf3%2Cf4%2Cf5%2Cf6&fields2=f51%2Cf52%2Cf53%2Cf54%2Cf55%2Cf56%2Cf57%2Cf58%2Cf59%2Cf60%2Cf61&klt={freq}&fqt=1&end={endDate}&lmt=1488";
    }

    private List<Quote> ParseKlineData(string response)
    {
        var quotes = new List<Quote>();
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
                dataElement.ValueKind == JsonValueKind.Null ||
                !dataElement.TryGetProperty("klines", out var klines))
            {
                return new List<Quote>();
            }

            foreach (var k in klines.EnumerateArray())
            {
                var str = k.GetString();
                if (string.IsNullOrEmpty(str)) continue;
                var parts = str.Split(',');
                if (parts.Length < 6) continue;
                var dateStr = parts[0];

                DateTime date;
                if (dateStr.Length == 10)
                {
                    if (!DateTime.TryParse($"{dateStr} 15:00", out date))
                        continue;
                }
                else
                {
                    if (!DateTime.TryParse(dateStr, out date))
                        continue;
                }

                quotes.Add(new Quote(
                    0,
                    date,
                    decimal.TryParse(parts[1], out var open) ? open : 0,
                    decimal.TryParse(parts[3], out var high) ? high : 0,
                    decimal.TryParse(parts[4], out var low) ? low : 0,
                    decimal.TryParse(parts[2], out var close) ? close : 0,
                    decimal.TryParse(parts[5], out var volume) ? volume : 0
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "东方财富K线数据解析失败");
        }

        return quotes;
    }
}
