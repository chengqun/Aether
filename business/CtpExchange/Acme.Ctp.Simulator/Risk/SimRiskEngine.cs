using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>下单前的风控检查上下文（由核心组装，风控不直接碰状态）。</summary>
public sealed class RiskContext
{
    public required CtpOrderRequest Request { get; init; }
    public required SimInstrument Instrument { get; init; }
    /// <summary>本单需冻结的保证金（按委托价或对手价估算，由核心算好）。</summary>
    public required double RequiredMargin { get; init; }
    /// <summary>本单需冻结的手续费。</summary>
    public required double RequiredFee { get; init; }
    /// <summary>当前可用资金。</summary>
    public required double Available { get; init; }
    /// <summary>开仓单：null；平仓单：该方向可平手数（已扣挂单占用）。</summary>
    public int? ClosableVolume { get; init; }
    /// <summary>是否处于交易时段（仅在启用时段闸门时被检查）。</summary>
    public bool InTradingTime { get; init; } = true;
}

/// <summary>风控引擎（可替换：接入更复杂的规则时换实现即可）。</summary>
public interface IRiskEngine
{
    /// <summary>检查一笔委托。返回 <see cref="RiskResult.Ok"/> 为 false 时，核心直接拒单。</summary>
    RiskResult CheckOrder(RiskContext context);
}

/// <summary>
/// Phase 1 风控：逐项校验（合约 / 手数 / 价格 / 平仓量 / 资金 / 时段）。
/// 未做：自成交检查、报单频率限制、持仓限额、单笔最大手数——留待后续按需扩展。
/// </summary>
public sealed class SimRiskEngine : IRiskEngine
{
    private readonly SimOptions _options;

    public SimRiskEngine(SimOptions options) => _options = options;

    public RiskResult CheckOrder(RiskContext context)
    {
        var req = context.Request;

        if (_options.RejectOrdersOutsideTradingTime && !context.InTradingTime)
            return RiskResult.Reject(SimErrorCodes.NotTradingTime, "当前非交易时段，报单被拒绝。");

        if (string.IsNullOrWhiteSpace(req.InstrumentId))
            return RiskResult.Reject(SimErrorCodes.UnknownInstrument, "合约代码为空。");

        if (req.Volume <= 0)
            return RiskResult.Reject(SimErrorCodes.InvalidVolume, $"报单手数非法：{req.Volume}。");

        if (req.PriceType == CtpOrderPriceType.Limit && req.LimitPrice <= 0)
            return RiskResult.Reject(SimErrorCodes.InvalidPrice, $"限价单价格非法：{req.LimitPrice:F4}。");

        // 现货开空解禁（2026-10-10 用户拍板「现货也可以做空，一个品种就够」）：现货走与期货一致的双向持仓。
        // 已知边界（用户确认先不管）：空头凭空开 ⇒ 买入方的 Long 持仓可超出链上真实币量 ⇒ 提币时
        // 链上 custody 余额不足会被链拒（余额守恒是最后防线——诚实失败，不崩账本）。

        if (IsClose(req.OffsetFlag))
        {
            var closable = context.ClosableVolume ?? 0;
            if (closable <= 0)
                return RiskResult.Reject(SimErrorCodes.NoPositionToClose,
                    $"平仓失败：{req.InstrumentId} 无可平持仓（{OffsetName(req.OffsetFlag)}）。");
            if (req.Volume > closable)
                return RiskResult.Reject(SimErrorCodes.CloseVolumeExceeds,
                    $"平仓手数超过可平量：报单 {req.Volume} 手，可平 {closable} 手。");
        }

        var need = context.RequiredMargin + context.RequiredFee;
        if (need > context.Available + 1e-6)
            return RiskResult.Reject(SimErrorCodes.InsufficientFunds,
                $"资金不足：需保证金 {context.RequiredMargin:F2} + 手续费 {context.RequiredFee:F2}，" +
                $"可用 {context.Available:F2}。");

        return RiskResult.Pass();
    }

    internal static bool IsClose(CtpOffsetFlag flag)
        => flag is CtpOffsetFlag.Close or CtpOffsetFlag.CloseToday or CtpOffsetFlag.CloseYesterday;

    internal static string OffsetName(CtpOffsetFlag flag) => flag switch
    {
        CtpOffsetFlag.Open => "开仓",
        CtpOffsetFlag.Close => "平仓",
        CtpOffsetFlag.CloseToday => "平今",
        CtpOffsetFlag.CloseYesterday => "平昨",
        _ => flag.ToString(),
    };
}
