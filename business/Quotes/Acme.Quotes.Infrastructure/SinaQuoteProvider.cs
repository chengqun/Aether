using Acme.Quotes.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
        QuoteHttpHeaders.ApplyBrowserUserAgent(_httpClient);
    }

    public bool IsSupported(string code, string freq, string type, string exchange)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(freq) || string.IsNullOrWhiteSpace(type))
            return false;

        return type == "期货";
    }

    public async Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange, CancellationToken ct = default)
    {
        var url = BuildRequestUrl(code, freq, type, exchange);

        // 简单重试：最多3次，500ms间隔，仅对 HttpRequestException 重试
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var response = await _httpClient.GetByteArrayAsync(url, ct);
                var content = Encoding.GetEncoding("GBK").GetString(response);
                return ParseKlineData(content);
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "新浪行情请求失败 (尝试 {Attempt}/3)，500ms 后重试: {Url}", attempt, url);
                await Task.Delay(500, ct);
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
            // 新浪 JSONP：/*<script>...</script>*/=([ {...}, {...} ]);
            // 取 =(...) 内的 JSON 数组。Singleline 兼容换行。
            string jsonData = Regex.Match(content, @"=\((.*)\);", RegexOptions.Singleline).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(jsonData))
                return quotes;

            using var doc = JsonDocument.Parse(jsonData);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // 当前接口返回对象数组：{d(日期),o(open),h(high),l(low),c(close),v(volume),...}
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (!el.TryGetProperty("d", out var dEl)) continue;
                    if (!DateTime.TryParse(dEl.GetString(), out var date)) continue;
                    quotes.Add(new Quote(
                        0, date,
                        GetDec(el, "o"),
                        GetDec(el, "h"),
                        GetDec(el, "l"),
                        GetDec(el, "c"),
                        GetDec(el, "v")));
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    // 兼容旧格式：[date, open, close, high, low, volume]
                    var parts = el.EnumerateArray().Select(x => x.GetString()).ToList();
                    if (parts.Count < 6) continue;
                    if (!DateTime.TryParse(parts[0], out var date)) continue;
                    quotes.Add(new Quote(
                        0, date,
                        decimal.TryParse(parts[1], out var o) ? o : 0,
                        decimal.TryParse(parts[3], out var h) ? h : 0,
                        decimal.TryParse(parts[4], out var l) ? l : 0,
                        decimal.TryParse(parts[2], out var c) ? c : 0,
                        decimal.TryParse(parts[5], out var v) ? v : 0));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "新浪K线数据解析失败");
        }

        return quotes;
    }

    private static decimal GetDec(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.String)
            return decimal.TryParse(v.GetString(), out var d) ? d : 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var num))
            return num;
        return 0;
    }
}
