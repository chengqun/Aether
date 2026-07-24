using Acme.Quotes.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Acme.Quotes.Infrastructure;

public class SinaQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SinaQuoteProvider> _logger;

    public SinaQuoteProvider(HttpClient httpClient, ILogger<SinaQuoteProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    public bool IsSupported(string code, string freq, string type, string exchange)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(freq) || string.IsNullOrWhiteSpace(type))
            return false;

        return type == "期货";
    }

    public async Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange)
    {
        var url = BuildRequestUrl(code, freq, type, exchange);

        // 简单重试：最多3次，500ms间隔，仅对 HttpRequestException 重试
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var response = await _httpClient.GetByteArrayAsync(url);
                var content = Encoding.GetEncoding("GBK").GetString(response);
                return ParseKlineData(content);
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "新浪行情请求失败 (尝试 {Attempt}/3)，500ms 后重试: {Url}", attempt, url);
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "新浪行情获取失败: {Url}", url);
                return new List<Quote>();
            }
        }

        // 第3次重试也失败
        _logger.LogWarning("新浪行情最终失败 (3次重试后): {Url}", url);
        return new List<Quote>();
    }

    private string BuildRequestUrl(string code, string freq, string type, string exchange)
    {
        string freqParam = freq switch
        {
            "101" => "getDailyKLine",
            _ => "getFewMinLine"
        };
        return $"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/=/InnerFuturesNewService.{freqParam}?symbol={code}&type={freq}";
    }

    private List<Quote> ParseKlineData(string content)
    {
        var quotes = new List<Quote>();
        try
        {
            string jsonData = Regex.Match(content, @"=\((.*)\);").Groups[1].Value;
            var data = JsonSerializer.Deserialize<List<List<object>>>(jsonData);

            if (data != null)
            {
                foreach (var item in data)
                {
                    if (item.Count >= 6)
                    {
                        var date = DateTime.Parse(item[0]?.ToString() ?? "");
                        var open = decimal.TryParse(item[1]?.ToString(), out var o) ? o : 0;
                        var close = decimal.TryParse(item[2]?.ToString(), out var c) ? c : 0;
                        var high = decimal.TryParse(item[3]?.ToString(), out var h) ? h : 0;
                        var low = decimal.TryParse(item[4]?.ToString(), out var l) ? l : 0;
                        var volume = decimal.TryParse(item[5]?.ToString(), out var v) ? v : 0;

                        quotes.Add(new Quote(0, date, open, high, low, close, volume));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "新浪K线数据解析失败");
        }

        return quotes;
    }
}
