using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 仿真柜台错误码。
/// <para>
/// <b>刻意自定义</b>：不与 CTP 官方错误码表对齐——真码表（<c>CTP 官方 3.x 错误码</c>）无法离线核实，
/// 假造一组「看起来像官方」的数字会误导排查。客户的 UI 只展示 <c>ErrorMsg</c>（中文），
/// <c>ErrorID</c> 仅用于「非 0 = 失败」判定，故自定义码值是安全的。
/// </para>
/// 保留 <c>0 = 成功</c> 与官方一致。
/// </summary>
public static class SimErrorCodes
{
    public const int Success = 0;

    /// <summary>未登录 / 会话已失效。</summary>
    public const int NotLoggedIn = 90001;
    /// <summary>未知合约（合约表里没有且不允许合成）。</summary>
    public const int UnknownInstrument = 90002;
    /// <summary>报单手数非法（≤0）。</summary>
    public const int InvalidVolume = 90003;
    /// <summary>限价单价格非法（≤0）。</summary>
    public const int InvalidPrice = 90004;
    /// <summary>资金不足（保证金 + 手续费 > 可用）。</summary>
    public const int InsufficientFunds = 90005;
    /// <summary>平仓无可用持仓。</summary>
    public const int NoPositionToClose = 90006;
    /// <summary>平仓手数超过可平量。</summary>
    public const int CloseVolumeExceeds = 90007;
    /// <summary>报单不存在（撤单时定位不到）。</summary>
    public const int OrderNotFound = 90008;
    /// <summary>报单状态不可撤（已全成 / 已撤）。</summary>
    public const int OrderNotCancelable = 90009;
    /// <summary>非交易时段（仅在启用时段闸门时返回）。</summary>
    public const int NotTradingTime = 90010;
    /// <summary>参数非法（方向/开平标志不合法组合）。</summary>
    public const int InvalidParameter = 90011;
    /// <summary>无对手价：交易所模式下市价单对手侧簿空（无庄家兜底——真实交易所语义）。90012-90015 为传输层保留段，故从 90016 续。</summary>
    public const int NoCounterparty = 90016;
    /// <summary>凭据不匹配 / 登录锁定（已设密码的账户密码错误，或连续失败触发防爆破锁定）。</summary>
    public const int BadCredentials = 90017;
    /// <summary>post-only 限价单插入时会吃单（对手簿在限价内有他人挂单）⇒ 整单拒收（2026-10-05 BTC 对标）。</summary>
    public const int PostOnlyWouldCross = 90018;
    /// <summary>提币失败/不可用：链桥未配置（custody seed 未设）、venue 现货持仓不足、或链节点拒绝——消息透传具体原因。</summary>
    public const int WithdrawalFailed = 90019;
}

/// <summary>风控判定结果。</summary>
public readonly record struct RiskResult(bool Ok, int ErrorId, string Message)
{
    public static RiskResult Pass() => new(true, SimErrorCodes.Success, "");
    public static RiskResult Reject(int errorId, string message) => new(false, errorId, message);
}
