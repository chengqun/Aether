using System.Net;
using Acme.Ctp.Front;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Front.Tests;

/// <summary>
/// 测试进程内回环：<see cref="SimTradingCore"/> + <see cref="CtpFrontServer"/>（端口 0 临时端口）——
/// 官方 SDK 客户端（<c>CtpTrader</c>，P/Invoke 官方 C++ 库）经真实 TCP loopback 走完整线协议一圈。
/// 镜像 <c>Acme.Ctp.Simulator.Tests</c> 的 <c>SimHarness.LoopbackSim</c> 范式。
/// </summary>
public sealed class FrontLoopback : IDisposable
{
    public const string Broker = "9999";
    public const double InitialBalance = 1_000_000;

    /// <summary>确定性时钟：2026-09-23 周三 10:00（日盘）⇒ <c>TradingDay == "20260923"</c>。</summary>
    public static readonly DateTime TradingNow = new(2026, 9, 23, 10, 0, 0);

    public SimTradingCore Core { get; }

    public CtpFrontServer Server { get; }

    /// <summary>官方 SDK 直连地址（tcp:// 前缀——<c>CtpConnectRequest.FrontAddress</c> 契约）。</summary>
    public string FrontAddress { get; }

    /// <summary>服务端逐帧日志的实时落盘路径（xunit 会扣留 Console 输出到测试结束——挂死时唯一诊断面）。</summary>
    public string LogPath { get; }

    private FrontLoopback(SimTradingCore core, CtpFrontServer server, string frontAddress, string logPath)
    {
        Core = core;
        Server = server;
        FrontAddress = frontAddress;
        LogPath = logPath;
    }

    public static FrontLoopback Start()
    {
        var options = new SimOptions
        {
            TickIntervalMs = 0,     // 不自动喂行情——本测试只走交易腿
            DatabasePath = null,    // 纯内存——绝不碰 ~/.aether/ctp-sim.db（用户真实账本）
            MarketMode = SimMarketMode.Walk,
        };
        var core = new SimTradingCore(options, new SimClock(() => TradingNow));
        core.InitializeAsync().GetAwaiter().GetResult(); // xunit 无同步上下文，安全
        // M2：Walk 模式庄家报价（激进限价在 InsertOrder 内同步全成、成交价=Ask——OrderLifecycleTests 先例）
        core.InjectQuote("m2701", 2999, 3001, 3000);   // m2701 = 5 字符（推送模板捕获槽宽，等宽纪律）

        // 双写日志：Console（xunit 测试结束时随结果输出）+ 实时文件（xunit 扣留 Console 到测试结束——
        // 测试挂死时文件是唯一的诊断面；服务端逐帧日志是「模板偏移错/签名漏配」定位依据）。
        var logPath = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", $"front-{Guid.NewGuid():N}.log");
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var server = new CtpFrontServer(core, "127.0.0.1", 0, msg =>
        {
            Console.WriteLine($"[CTP-Front] {msg}");
            logWriter.WriteLine(msg);
        });
        server.Start();
        var port = (server.LocalEndpoint as IPEndPoint)?.Port
            ?? throw new InvalidOperationException("CtpFrontServer 未绑定端口");
        return new FrontLoopback(core, server, $"tcp://127.0.0.1:{port}", logPath);
    }

    public void Dispose()
    {
        try { Server.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
        Console.WriteLine($"[CTP-Front] 本测试服务端日志: {LogPath}");
    }
}
