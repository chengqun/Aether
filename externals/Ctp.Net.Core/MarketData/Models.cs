using Ctp.Net.Core.Native;

namespace Ctp.Net.Core.MarketData;

/// <summary>Tick 行情（从 DepthMarketDataField 映射的公共模型）。</summary>
public sealed record TickData
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string TradingDay { get; init; } = "";
    public string ActionDay { get; init; } = "";
    public string UpdateTime { get; init; } = "";       // HH:mm:ss
    public int UpdateMillisec { get; init; }
    public double LastPrice { get; init; }
    public long Volume { get; init; }
    public double OpenInterest { get; init; }
    public double Turnover { get; init; }
    public double BidPrice { get; init; }
    public int BidVolume { get; init; }
    public double AskPrice { get; init; }
    public int AskVolume { get; init; }
    public double PreSettlement { get; init; }
    public double PreClose { get; init; }
    public double OpenPrice { get; init; }
    public double Highest { get; init; }
    public double Lowest { get; init; }

    public static TickData From(in DepthMarketDataField f) => new()
    {
        InstrumentId = f.InstrumentID,
        ExchangeId = f.ExchangeID,
        TradingDay = f.TradingDay,
        ActionDay = f.ActionDay,
        UpdateTime = f.UpdateTime,
        UpdateMillisec = f.UpdateMillisec,
        LastPrice = f.LastPrice,
        Volume = f.Volume,
        OpenInterest = f.OpenInterest,
        Turnover = f.Turnover,
        BidPrice = f.BidPrice1, BidVolume = f.BidVolume1,
        AskPrice = f.AskPrice1, AskVolume = f.AskVolume1,
        PreSettlement = f.PreSettlementPrice,
        PreClose = f.PreClosePrice,
        OpenPrice = f.OpenPrice,
        Highest = f.HighestPrice,
        Lowest = f.LowestPrice,
    };

    public override string ToString() =>
        $"{InstrumentId} {UpdateTime}.{UpdateMillisec:D3} " +
        $"last={LastPrice} vol={Volume} oi={OpenInterest} " +
        $"bid={BidPrice}×{BidVolume} ask={AskPrice}×{AskVolume}";
}
