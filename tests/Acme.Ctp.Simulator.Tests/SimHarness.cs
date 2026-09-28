using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure.Sim;
using Acme.Ctp.Sim.Server;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 单测夹具：确定性时钟 + 纯内存存储 + 手工注入的盘口。
/// <para>
/// 三条确定性来源：①<see cref="SimOptions.TickIntervalMs"/> = 0（行情只在测试显式调用
/// <c>AdvanceMarket</c> 时变化）；②<see cref="SimOptions.DatabasePath"/> = null（不落盘）；
/// ③固定时钟指向交易时段内的一个周三。
/// </para>
/// </summary>
internal static class SimHarness
{
    /// <summary>2026-09-23 是周三，10:00 落在日盘 [09:00,10:15) 内 ⇒ <c>IsTradingTime</c> 为 true。</summary>
    public static readonly DateTime TradingNow = new(2026, 9, 23, 10, 0, 0);

    public const string Instrument = "ag2610";
    public const string Account = "tester";
    public const string Broker = "9999";

    /// <summary>ag2610 的交易所（品种种子表里 ag 属上期所）——撤单定位键的回显项。</summary>
    public const string Exchange = "SHFE";

    /// <summary>注入的盘口（tick = 1 ⇒ 原样往返，见 <c>InjectQuote</c> 的 Mid 语义）。</summary>
    public const double Bid = 6999;
    public const double Ask = 7001;
    public const double Last = 7000;

    /// <summary>ag2610 的合约元数据（<c>InstrumentCatalog</c> 种子）：乘数 15 / 保证金率 12% / 变动价位 1。</summary>
    public const double VolumeMultiple = 15;
    public const double MarginRate = 0.12;
    public const double CommissionRate = 0.00005;

    public const double InitialBalance = 1_000_000;

    /// <summary>测试登录密码（密码必填——2026-09-28 起 `Login` 无密码一律拒 90017）。</summary>
    public const string TestPassword = "test-pw";

    /// <summary>默认参数：不自动跑行情、不落盘、**显式 walk**（MarketMode 默认已是 Exchange——2026-09-28 翻转；本 harness 大量用例测 walk 撮合语义）。</summary>
    public static SimOptions Options() => new() { TickIntervalMs = 0, DatabasePath = null, MarketMode = SimMarketMode.Walk };

    public static async Task<SimTradingCore> NewCoreAsync(
        SimOptions? options = null,
        bool injectQuote = true,
        DateTime? now = null)
    {
        var core = new SimTradingCore(options ?? Options(), new SimClock(() => now ?? TradingNow));
        await core.InitializeAsync();
        if (injectQuote) core.InjectQuote(Instrument, Bid, Ask, Last);
        return core;
    }

    public static CtpLoginResult Login(SimTradingCore core) => core.Login(Broker, Account, TestPassword);

    /// <summary>已登录 + 已注入盘口的核心（绝大多数用例的起点）。</summary>
    public static async Task<SimTradingCore> NewLoggedInCoreAsync(SimOptions? options = null, DateTime? now = null)
    {
        var core = await NewCoreAsync(options, now: now);
        Login(core);
        return core;
    }

    public static CtpOrderRequest Limit(
        CtpDirection direction,
        int volume,
        double price,
        CtpOffsetFlag offset = CtpOffsetFlag.Open,
        CtpTimeCondition timeCondition = CtpTimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = CtpOrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static CtpOrderRequest Market(
        CtpDirection direction,
        int volume,
        CtpOffsetFlag offset = CtpOffsetFlag.Open,
        CtpTimeCondition timeCondition = CtpTimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = CtpOrderPriceType.Market,
            LimitPrice = 0,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static CtpOrderRef RefOf(CtpOrder order)
        => new(Broker, Account, order.FrontId, order.SessionId, order.OrderRef, order.ExchangeId);
}

/// <summary>
/// 线程安全的回报收集器——回调由泵线程（<c>SimCtpSession</c>）或调用线程（<c>SimTradingCore</c>）触发，
/// 断言时必须先取快照。
/// </summary>
internal sealed class Collector<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_gate) _items.Add(item);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public IReadOnlyList<T> Snapshot
    {
        get { lock (_gate) return _items.ToList(); }
    }
}

internal static class Wait
{
    /// <summary>轮询等待谓词成立（回调是异步外发的，不能同步断言）。</summary>
    public static async Task<bool> UntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }

    /// <summary>静默期观察：等待 <paramref name="quietMs"/> 后确认计数未再变化（用于「恰好一次」断言）。</summary>
    public static async Task<int> QuiesceAsync(Func<int> count, int quietMs = 250)
    {
        await Task.Delay(quietMs);
        return count();
    }
}

/// <summary>
/// 回环仿真：测试进程内起「host 侧」listener + <see cref="RemoteCtpSession"/>——把 Phase 1 的进程内适配测试
/// 升级为**跨线**契约测试（同一套断言经 TCP/JSON 走一圈，传输层顺带被覆盖），无需外部进程。
/// 生命周期镜像真实 host：InitializeAsync + StartMarketFeed 在构造时一次（feed = host 生命周期），
/// Dispose = 关会话 + 停 listener + core.Dispose（同步 flush——重启续用测试的落盘点）。
/// </summary>
internal sealed class LoopbackSim : IDisposable
{
    public SimTradingCore Core { get; }
    public SimRpcServer Server { get; }
    public RemoteCtpSession Session { get; }

    private LoopbackSim(SimTradingCore core, SimRpcServer server, RemoteCtpSession session)
    {
        Core = core;
        Server = server;
        Session = session;
    }

    /// <summary>启动回环柜台（端口 0 = 内核临时端口；configure 塑造**服务端** options）。</summary>
    public static LoopbackSim Start(Func<SimOptions, SimOptions>? configure = null)
    {
        var options = configure is null ? SimHarness.Options() : configure(SimHarness.Options());
        var core = new SimTradingCore(options);
        core.InitializeAsync().GetAwaiter().GetResult();   // xunit 无同步上下文 ⇒ sync-over-async 安全
        core.StartMarketFeed();
        var server = new SimRpcServer(core, "127.0.0.1", 0);
        server.Start();
        var session = new RemoteCtpSession("127.0.0.1", server.LocalEndpoint!.Port);
        return new LoopbackSim(core, server, session);
    }

    public void Dispose()
    {
        try { Session.Dispose(); } catch { }
        try { Server.Dispose(); } catch { }   // StopAsync + 关连接（不 dispose core——下行）
        try { Core.Dispose(); } catch { }     // 停 feed + 同步 flush
    }
}
