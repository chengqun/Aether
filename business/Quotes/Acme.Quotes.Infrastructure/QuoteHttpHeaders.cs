using System.Net.Http;

namespace Acme.Quotes.Infrastructure;

/// <summary>
/// 共享的行情 HTTP 请求头。新浪/东方财富/百度行情接口均会拒绝无 User-Agent 的请求
/// （百度 403 Forbidden、东方财富 WAF 直接断连致 <c>HttpConnection.SendAsync</c> 失败），
/// 故所有 provider 在构造时套上浏览器 UA。幂等——重复调用安全（typed-client 复用同一 HttpClient）。
/// </summary>
public static class QuoteHttpHeaders
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>给 HttpClient 套上浏览器 User-Agent（幂等）。</summary>
    public static void ApplyBrowserUserAgent(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }
}
