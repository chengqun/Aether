using Acme.Quotes.Application;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Domain;
using Acme.Quotes.Infrastructure;
using Acme.Quotes.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Acme.Quotes.AI.Tests;

/// <summary>
/// 真实网络集成测试：直接打 Sina/East/Baidu 行情接口，验证 K 线获取链路。
/// 这些测试打外网（非 mock），用于定位"获取行情有问题"的实际故障点。
/// 失败信息会打印每个 provider 的返回条数/首尾 quote/异常，便于排查。
/// </summary>
public class QuoteProviderNetworkTests
{
    private readonly ITestOutputHelper _out;
    public QuoteProviderNetworkTests(ITestOutputHelper outHelper) => _out = outHelper;

    private static HttpClient Http() => new();

    private ILogger<T> Log<T>() => new XUnitLogger<T>(_out);

    private void Dump(string label, List<Quote> quotes)
    {
        if (quotes.Count == 0) { _out.WriteLine($"{label}: 空（0 条）"); return; }
        var first = quotes[0];
        var last = quotes[^1];
        _out.WriteLine($"{label}: {quotes.Count} 条 | 首 {first.Date:yyyy-MM-dd} O={first.Open} C={first.Close} H={first.High} L={first.Low} V={first.Volume} | 末 {last.Date:yyyy-MM-dd} C={last.Close}");
    }

    /// <summary>
    /// 探测目标主机是否可达（短超时 GET）。这些是真实网络集成测试——单 provider 测试在主机不可达时
    /// （如东方财富 WAF 封 IP、外网不通）不应判失败，只在「主机可达但我们的代码拿不到数据」时才失败。
    /// </summary>
    private async Task<bool> ReachableAsync(string url)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            QuoteHttpHeaders.ApplyBrowserUserAgent(c);
            // 任意响应（含 4xx/5xx）都说明 TCP/TLS 握手成功 = 主机可达；只有抛异常才算不可达。
            _ = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch (Exception ex)
        {
            _out.WriteLine($"[probe] 主机不可达，跳过断言：{url}\n  {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── 单 provider 直打 ──

    [Fact]
    public async Task Sina_fetches_futures_kline()
    {
        const string probeUrl = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/=/InnerFuturesNewService.getDailyKLine?symbol=ag2610&type=101";
        if (!await ReachableAsync(probeUrl)) return;
        var p = new SinaQuoteProvider(Http(), Log<SinaQuoteProvider>());
        Assert.True(p.IsSupported("ag2610", "101", "期货", ""), "Sina 应支持期货");
        List<Quote> quotes;
        try { quotes = await p.GetKlineAsync("ag2610", "101", "期货", ""); }
        catch (Exception ex) { _out.WriteLine($"Sina 异常: {ex}"); throw; }
        Dump("Sina ag2610 期货 日线", quotes);
        Assert.True(quotes.Count > 0, "Sina 应返回非空 K 线");
    }

    [Fact]
    public async Task East_fetches_stock_kline()
    {
        const string probeUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=1.600000&fields1=f1&fields2=f51&klt=101&fqt=1&end=20260812&lmt=5";
        if (!await ReachableAsync(probeUrl)) return;
        var p = new EastQuoteProvider(Http(), Log<EastQuoteProvider>());
        Assert.True(p.IsSupported("600000", "101", "股票", "1"), "East 应支持股票");
        List<Quote> quotes;
        try { quotes = await p.GetKlineAsync("600000", "101", "股票", "1"); }
        catch (Exception ex) { _out.WriteLine($"East 异常: {ex}"); throw; }
        Dump("East 600000 股票 日线", quotes);
        Assert.True(quotes.Count > 0, "East 应返回非空 K 线");
    }

    [Fact]
    public async Task Baidu_fetches_stock_kline()
    {
        const string probeUrl = "https://finance.pae.baidu.com/vapi/v1/getquotation?group=quotation_kline_ab&ktype=day&code=600000&market_type=ab";
        if (!await ReachableAsync(probeUrl)) return;
        var p = new BaiduQuoteProvider(Http(), Log<BaiduQuoteProvider>());
        Assert.True(p.IsSupported("600000", "101", "股票", ""), "Baidu 应支持股票日线");
        List<Quote> quotes;
        try { quotes = await p.GetKlineAsync("600000", "101", "股票", ""); }
        catch (Exception ex) { _out.WriteLine($"Baidu 异常: {ex}"); throw; }
        Dump("Baidu 600000 股票 日线", quotes);
        Assert.True(quotes.Count > 0, "Baidu 应返回非空 K 线");
    }

    // ── Failover 聚合 ──

    [Fact]
    public async Task Failover_fetches_stock_kline()
    {
        FailoverQuoteProvider.ClearAllFailures();
        var providers = new IQuoteProvider[]
        {
            new SinaQuoteProvider(Http(), Log<SinaQuoteProvider>()),
            new EastQuoteProvider(Http(), Log<EastQuoteProvider>()),
            new BaiduQuoteProvider(Http(), Log<BaiduQuoteProvider>()),
        };
        var failover = new FailoverQuoteProvider(providers, Log<FailoverQuoteProvider>());
        List<Quote> quotes = await failover.GetKlineAsync("600000", "101", "股票", "1");
        Dump("Failover 600000 股票 日线", quotes);
        Assert.True(quotes.Count > 0, "Failover 应至少有一个 provider 返回非空 K 线");
    }

    // ── AnalysisManager 全链路（fake 安全查找 + 真 providers + 临时 SQLite 缓存）──

    [Fact]
    public async Task AnalysisManager_fetches_stock_kline()
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), $"quotes-test-{Guid.NewGuid():N}.db");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddQuotesInfrastructure(sqliteDbPath: tmpDb);
            builder.Services.AddQuotesApplication();
            builder.Services.AddSingleton<ISecurityLookup>(new FakeSecurityLookup());
            using var app = builder.Build();
            var analysis = app.Services.GetRequiredService<IAnalysisManager>();

            var (quotes, security, error) = await analysis.GetKlineAsync("600000", "101");
            _out.WriteLine($"AnalysisManager 600000 → security={security?.Code}/{security?.Type}/{security?.Exchange} error={error}");
            if (quotes is not null) Dump("AnalysisManager 600000 日线", quotes);
            Assert.Null(error);
            Assert.NotNull(quotes);
            Assert.True(quotes!.Count > 0, "AnalysisManager 应经 failover 拿到非空 K 线");
        }
        finally { if (File.Exists(tmpDb)) try { File.Delete(tmpDb); } catch { } }
    }

    private sealed class FakeSecurityLookup : ISecurityLookup
    {
        public Task<List<SecurityRef>> SearchAsync(string keyword) => Task.FromResult(new List<SecurityRef>
        {
            new() { Id = 1, Code = "600000", Name = "浦发银行", Type = "股票", Exchange = "1" },
        });
    }
}

/// <summary>把 provider 日志转发到 xUnit 输出，便于排查 failover 各步。</summary>
file sealed class XUnitLogger<T> : ILogger<T>
{
    private readonly ITestOutputHelper _out;
    public XUnitLogger(ITestOutputHelper o) => _out = o;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel ll) => ll >= LogLevel.Debug;
    public void Log<TState>(LogLevel ll, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
    {
        try { _out.WriteLine($"[{typeof(T).Name}] {fmt(state, ex)}"); if (ex is not null) _out.WriteLine(ex.ToString()); }
        catch { /* 输出已结束等，忽略 */ }
    }
}

file sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    public void Dispose() { }
}

