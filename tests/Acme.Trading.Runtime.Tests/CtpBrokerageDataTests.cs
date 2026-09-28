using Acme.Ctp.Domain;
using Acme.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Data;
using Xunit;

namespace Acme.Trading.Runtime.Tests;

/// <summary>
/// 实盘数据面单测（FakeCtpSession 驱动 tick → BarBuilder → aggregator 枚举）：
/// 2026-09-28 修复的回归——tick/second 分辨率下**首个 tick 必须立即成棒**。
/// 交易所模式行情是事件驱动（挂单/撤单/成交才有 tick），旧语义「等下一 tick 关上一根棒」会让订阅快照
/// （空闲市场的唯一一帧）永远不成棒 ⇒ OnData 不触发 ⇒ 策略不下单 ⇒ 再无事件——市场自举死锁
/// （实测：双 bot 启动后零交易）。
/// 注意：ScannableEnumerator.MoveNext 恒返 true（Lean 语义——空集也 true），判据是 Current 是否为棒。
/// </summary>
public class CtpBrokerageDataTests : IDisposable
{
    private readonly CtpBrokerageTradingTests.FakeCtpSession _session = new();
    private readonly CtpBrokerage _brokerage;
    private readonly List<string> _logs = new();

    public CtpBrokerageDataTests()
    {
        CtpLeanGateway.LogSink = m => _logs.Add(m);
        CtpLeanGateway.Instruments = new[] { "ag2610" };
        CtpLeanGateway.MarketDataConnect = new CtpMarketDataConnect
        {
            MdFrontAddress = "tcp://127.0.0.1:40002", BrokerId = "9999", UserId = "user01", Password = "pw",
        };
        _brokerage = new CtpBrokerage(null, _session);
        _brokerage.Connect();   // WireSessionEvents（fake IsConnected=true）
    }

    public void Dispose()
    {
        CtpLeanGateway.Instruments = Array.Empty<string>();
        CtpLeanGateway.MarketDataConnect = null;
        CtpLeanGateway.LogSink = null;
    }

    private static SubscriptionDataConfig Config(Resolution resolution)
    {
        var symbol = Symbol.Create("ag2610", SecurityType.Base, Market.USA);
        return new SubscriptionDataConfig(typeof(CtpBar), symbol, resolution,
            TimeZones.NewYork, TimeZones.NewYork, false, false, false, isCustom: true);
    }

    private static CtpTick Tick(double last, string time) => new()
    {
        InstrumentId = "ag2610", LastPrice = last, UpdateTime = time, UpdateMillisec = 0,
        ActionDay = "20260928", TradingDay = "20260928",
    };

    [Fact]
    public void 首个tick立即成棒_交易所模式自举()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        // 空闲市场唯一的一帧：订阅快照 tick——必须立即产出 bar（不等下一 tick）
        _session.RaiseTick(Tick(7000, "10:00:00"));

        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);   // Current=null ⇒ IsType 失败（自举死锁会挂这里）
        Assert.Equal(7000m, bar.Close);
    }

    [Fact]
    public void 订阅快照在订阅往返内到达不被丢弃()
    {
        // 竞态回归（2026-09-28 实测第二轮）：服务端在订阅应答同拍推送快照 tick（接收线程触发
        // OnTickReceived）——旧序「先触发 CTP 订阅、后登记 _symbolByInstrument」会把它 TryGetValue 丢弃。
        // 注入钩子让 fake 在 SubscribeMarketDataAsync 返回前同步推一帧 = 确定性复现该时序。
        _session.OnSubscribe = (_, _) =>
        {
            _session.RaiseTick(Tick(7000, "10:00:00"));
            return Task.CompletedTask;
        };

        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);   // 快照未被丢弃且立即成棒
        Assert.Equal(7000m, bar.Close);
    }

    [Fact]
    public void 后续tick各成单点棒_无延迟()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        _session.RaiseTick(Tick(7000, "10:00:00"));
        _session.RaiseTick(Tick(7001, "10:00:01"));
        _session.RaiseTick(Tick(6999, "10:00:02"));

        var bars = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            enumerator.MoveNext();
            var bar = Assert.IsType<CtpBar>(enumerator.Current);
            bars.Add(bar.Close);
        }
        Assert.Equal(new[] { 7000m, 7001m, 6999m }, bars);   // 每个 tick 一根、即时、不滞后一根
    }

    [Fact]
    public void 分钟级仍是跨边界成棒()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Minute), (_, _) => { });

        // 同一分钟内的两个 tick：不产棒（bar 是时间聚合，须等边界过掉）
        _session.RaiseTick(Tick(7000, "10:00:01"));
        _session.RaiseTick(Tick(7002, "10:00:30"));
        enumerator.MoveNext();
        Assert.False(enumerator.Current is CtpBar, "分钟级在边界内不应成棒");

        // 跨边界：上一分钟聚合棒产出（O=7000 H=7002 L=7000 C=7002）
        _session.RaiseTick(Tick(7003, "10:01:05"));
        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);
        Assert.Equal(7000m, bar.Open);
        Assert.Equal(7002m, bar.High);
        Assert.Equal(7000m, bar.Low);
        Assert.Equal(7002m, bar.Close);
    }
}
