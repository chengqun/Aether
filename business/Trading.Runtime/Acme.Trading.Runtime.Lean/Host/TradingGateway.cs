namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// 共享算法的静态日志/停止网关。回测与实盘共用同一算法（TradingHostAlgorithm），算法需日志输出与停止信号；
/// 实盘侧由 LeanLiveTradingService 启动引擎前注入 LogSink + StopRequested，回测侧留空（回测日志走 Lean ResultHandler）。
/// 与 CtpLeanGateway（CTP 会话专属）分开——本类只管算法侧通用需求，不依赖 CTP。
/// </summary>
public static class TradingGateway
{
    /// <summary>日志接收口（实盘 service 转 UI）。为 null 时算法静默。</summary>
    public static Action<string>? LogSink { get; set; }

    /// <summary>停止标志——实盘算法 OnData 检测后调 Quit() 退出引擎。回测不检查（LiveMode=false）。</summary>
    public static volatile bool StopRequested;
    public static volatile bool VerboseLog;

    public static void Log(string message)
    {
        try { LogSink?.Invoke(message); }
        catch { /* 日志不应影响引擎 */ }
    }

    public static void Reset()
    {
        LogSink = null;
        StopRequested = false;
        VerboseLog = false;
    }
}
