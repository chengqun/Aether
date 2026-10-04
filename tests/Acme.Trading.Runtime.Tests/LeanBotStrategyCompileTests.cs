using System.IO;
using Acme.Trading.Runtime.Lean;
using QuantConnect.Algorithm;
using Xunit;

namespace Acme.Trading.Runtime.Tests;

/// <summary>
/// 交易所机器人策略（做市商 / 噪声交易者，Lean 版）的编译锁定——镜像
/// <c>~/Library/Application Support/Aether/Desktop-Stockapp/labs/leanalgorithms/{market-maker,noise-taker}.lean.cs</c>
/// （labs 是用户数据不进仓；改实验室文件时同步这里——同 <see cref="LeanAlgorithmTemplateCompileTests"/> 对模板的做法）。
/// 两个策略 = 「bots 即策略」路线（用户拍板）：机器人不是服务端内嵌代码，而是普通客户端账户上跑的
/// Lean 算法笔记，经 P2 真实下单（实盘 tab 勾「真实下单」）对着交易所交易。
/// </summary>
public class LeanBotStrategyCompileTests
{
    private const string MarketMakerSource = """
using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Orders;

namespace LabsLeanAlgorithms;

public class MarketMakerAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private decimal _last;
    private decimal _priceTick;
    private int _spreadTicks;
    private int _levelStepTicks;
    private int _levels;
    private decimal _quoteVolume;
    private decimal _positionCap;
    private readonly List<OrderTicket> _bidLegs = new();
    private readonly List<OrderTicket> _askLegs = new();

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef("SpreadTicks", "内侧点差（最内档距最新价 tick 数）", "2"),
        new StrategyParameterDef("Levels", "每侧档位数", "5"),
        new StrategyParameterDef("LevelStepTicks", "相邻档位间距（tick 数）", "1"),
        new StrategyParameterDef("PriceTick", "最小变动价位", "1"),
        new StrategyParameterDef("QuoteVolume", "单档挂单手数", "5"),
        new StrategyParameterDef("PositionCap", "单边持仓上限（手）", "200"),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();
        _symbol = AddCtpData(GetParameter("symbol") ?? "sim001", Resolution.Second);

        _priceTick = GetDecimalParam("PriceTick", 1m);
        _spreadTicks = GetIntParam("SpreadTicks", 2);
        _levelStepTicks = GetIntParam("LevelStepTicks", 1);
        _levels = GetIntParam("Levels", 5);
        _quoteVolume = GetDecimalParam("QuoteVolume", 5m);
        _positionCap = GetDecimalParam("PositionCap", 200m);
        BotLog($"[MM] 初始化 {_symbol.Value}: 上下各{_levels}档 点差{_spreadTicks}tick 档距{_levelStepTicks}tick 单档{_quoteVolume}手 仓限±{_positionCap}");
    }

    public override void OnData(Slice slice)
    {
        if (IsWarmingUp || !slice.TryGet<CtpBar>(_symbol, out var bar)) return;
        if (_last == 0) BotLog($"[MM] 首帧行情 last={bar.Close}（自举成功——数据链已通）");
        _last = bar.Close;
        RefreshQuotes();
    }

    public override void OnOrderEvent(OrderEvent e)
    {
        if (e.Status.IsClosed())
        {
            _bidLegs.RemoveAll(t => t.OrderId == e.OrderId);
            _askLegs.RemoveAll(t => t.OrderId == e.OrderId);
        }
        if (e.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            BotLog($"[MM] 成交 {e.Symbol.Value} {e.FillQuantity}@{e.FillPrice}（净持仓 {Portfolio[_symbol].Quantity}）");
    }

    private void RefreshQuotes()
    {
        var security = Portfolio[_symbol];
        var pos = security.Quantity;
        if (security.Price <= 0) return;

        if (pos > _positionCap) MarketOrder(_symbol, _positionCap - pos, tag: "超限减仓");
        else if (pos < -_positionCap) MarketOrder(_symbol, -_positionCap - pos, tag: "超限减仓");

        var desiredBids = DesiredLevels(bid: true);
        var desiredAsks = DesiredLevels(bid: false);

        RollStaleLegs(_bidLegs, desiredBids, pos >= _positionCap, "买");
        RollStaleLegs(_askLegs, desiredAsks, pos <= -_positionCap, "卖");

        FillMissingLegs(_bidLegs, desiredBids, _positionCap - pos, isBid: true);
        FillMissingLegs(_askLegs, desiredAsks, _positionCap + pos, isBid: false);
    }

    private List<decimal> DesiredLevels(bool bid)
    {
        var levels = new List<decimal>(_levels);
        for (var i = 0; i < _levels; i++)
        {
            var offset = (_spreadTicks + i * _levelStepTicks) * _priceTick;
            levels.Add(RoundToTick(bid ? _last - offset : _last + offset));
        }
        return levels;
    }

    private void RollStaleLegs(List<OrderTicket> legs, List<decimal> desired, bool capped, string side)
    {
        foreach (var leg in legs)
        {
            if (leg.Status == OrderStatus.CancelPending) continue;
            var price = leg.Get(OrderField.LimitPrice);
            if (capped || !desired.Contains(price))
            {
                BotLog($"[MM] 撤{side}腿 @{price}（移出带宽/触限）");
                leg.Cancel();
            }
        }
    }

    private void FillMissingLegs(List<OrderTicket> legs, List<decimal> desired, decimal budget, bool isBid)
    {
        if (budget <= 0) return;
        var resting = legs.Sum(t => Math.Abs(t.QuantityRemaining));
        foreach (var price in desired)
        {
            if (legs.Exists(t => t.Get(OrderField.LimitPrice) == price)) continue;
            var volume = Math.Min(_quoteVolume, budget - resting);
            if (volume < 1m) break;
            legs.Add(LimitOrder(_symbol, isBid ? volume : -volume, price, tag: isBid ? "MM买" : "MM卖"));
            resting += volume;
            BotLog($"[MM] 挂{(isBid ? "买" : "卖")} {price} × {volume}手（last={_last}）");
        }
    }

    private decimal RoundToTick(decimal price)
        => _priceTick > 0 ? Math.Round(price / _priceTick) * _priceTick : price;

    private decimal GetDecimalParam(string key, decimal fallback)
        => GetParameter(key) is { Length: > 0 } s && decimal.TryParse(s, out var v) && v > 0 ? v : fallback;

    private int GetIntParam(string key, int fallback)
        => GetParameter(key) is { Length: > 0 } s && int.TryParse(s, out var v) && v > 0 ? v : fallback;
}
""";

    private const string NoiseTakerSource = """
using System;
using System.Collections.Generic;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Algorithm;

namespace LabsLeanAlgorithms;

public class NoiseTakerAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private Random _rng = new(42);
    private int _maxLots;
    private int _netCap;

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef("IntervalSec", "交易间隔（秒）", "3"),
        new StrategyParameterDef("MaxLots", "单笔手数上限", "3"),
        new StrategyParameterDef("NetCap", "净持仓软钳（手）", "3"),
        new StrategyParameterDef("Seed", "随机种子（可复现）", "42"),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();
        _symbol = AddCtpData(GetParameter("symbol") ?? "sim001", Resolution.Second);

        var interval = GetIntParam("IntervalSec", 3);
        _maxLots = GetIntParam("MaxLots", 3);
        _netCap = GetIntParam("NetCap", 3);
        _rng = new Random(GetIntParam("Seed", 42));
        Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(Math.Max(1, interval))), TakeTurn);
        BotLog($"[Noise] 初始化 {_symbol.Value}: 每{interval}s 随机吃 ≤{_maxLots}手 净钳±{_netCap}");
    }

    private void TakeTurn()
    {
        var security = Portfolio[_symbol];
        if (security.Price <= 0)
        {
            BotLog("[Noise] 等待首帧行情（Price=0）——数据链未通则永远跳过");
            return;
        }

        var pos = security.Quantity;
        var dir = _rng.Next(2) == 0 ? 1 : -1;
        if (dir == 1 && pos >= _netCap) dir = -1;
        else if (dir == -1 && pos <= -_netCap) dir = 1;
        var lots = _rng.Next(1, _maxLots + 1);
        BotLog($"[Noise] 吃单 {dir * lots}手（last={security.Price} 净仓{pos}）");
        MarketOrder(_symbol, dir * lots, tag: "噪声吃单");
    }

    private int GetIntParam(string key, int fallback)
        => GetParameter(key) is { Length: > 0 } s && int.TryParse(s, out var v) && v > 0 ? v : fallback;
}
""";

    [Fact]
    public void MarketMaker_strategy_compiles()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lean-bot-mm-test");
        Directory.CreateDirectory(cacheDir);
        var ok = LeanAlgorithmScriptCompiler.TryCompile(MarketMakerSource, "market-maker", cacheDir, out var type, out _, out var err);
        Assert.True(ok, err ?? "(无错误信息)");
        Assert.NotNull(type);
        Assert.True(typeof(QCAlgorithm).IsAssignableFrom(type));
    }

    [Fact]
    public void NoiseTaker_strategy_compiles()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lean-bot-noise-test");
        Directory.CreateDirectory(cacheDir);
        var ok = LeanAlgorithmScriptCompiler.TryCompile(NoiseTakerSource, "noise-taker", cacheDir, out var type, out _, out var err);
        Assert.True(ok, err ?? "(无错误信息)");
        Assert.NotNull(type);
        Assert.True(typeof(QCAlgorithm).IsAssignableFrom(type));
    }
}
