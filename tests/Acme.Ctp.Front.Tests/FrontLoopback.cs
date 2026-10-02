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

    /// <summary>行情腿前置直连地址（M3 E2E 用——CtpMarketData SDK 客户端）。</summary>
    public string MdAddress { get; }

    /// <summary>官方 SDK 直连地址（tcp:// 前缀——<c>CtpConnectRequest.FrontAddress</c> 契约）。</summary>
    public string FrontAddress { get; }

    /// <summary>服务端逐帧日志的实时落盘路径（xunit 会扣留 Console 输出到测试结束——挂死时唯一诊断面）。</summary>
    public string LogPath { get; }

    private CtpFrontMdServer? _mdServer;

    private FrontLoopback(SimTradingCore core, CtpFrontServer server, string frontAddress, string logPath, CtpFrontMdServer mdServer, string mdAddress)
    {
        Core = core;
        Server = server;
        FrontAddress = frontAddress;
        LogPath = logPath;
        MdAddress = mdAddress;
        _mdServer = mdServer;
    }

    public static FrontLoopback Start() => Start(SimMarketMode.Walk);

    /// <summary>
    /// 交易所模式回环（双 bot E2E 用）：无庄家兜底、价格发现归订单簿、上市表 = m2701（官方命名 5 字符
    /// 对齐推送模板槽宽）。tick 由订阅快照/簿事件驱动——本类 TickIntervalMs=0 与交易所模式正交（事件驱动）。
    /// </summary>
    public static FrontLoopback StartExchange()
    {
        var loop = StartCore(SimMarketMode.Exchange);
        return loop;
    }

    private static FrontLoopback Start(SimMarketMode mode)
    {
        var loop = StartCore(mode);
        if (mode == SimMarketMode.Walk)
            loop.Core.InjectQuote("m2701", 2999, 3001, 3000);   // m2701 = 5 字符（推送模板捕获槽宽，等宽纪律）
        return loop;
    }

    private static FrontLoopback StartCore(SimMarketMode mode)
    {
        var options = new SimOptions
        {
            TickIntervalMs = 0,     // 不自动喂行情——本测试只走交易腿
            DatabasePath = null,    // 纯内存——绝不碰 ~/.aether/ctp-sim.db（用户真实账本）
            MarketMode = mode,
            ListedInstruments = mode == SimMarketMode.Exchange ? new[] { "m2701" } : null,
        };
        var core = new SimTradingCore(options, new SimClock(() => TradingNow));
        core.InitializeAsync().GetAwaiter().GetResult(); // xunit 无同步上下文，安全

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
        var mdServer = new CtpFrontMdServer(core, "127.0.0.1", 0, msg =>
        {
            Console.WriteLine($"[CTP-Front-MD] {msg}");
            logWriter.WriteLine(msg);
        });
        mdServer.Start();
        var mdPort = (mdServer.LocalEndpoint as IPEndPoint)?.Port
            ?? throw new InvalidOperationException("CtpFrontMdServer 未绑定端口");
        return new FrontLoopback(core, server, $"tcp://127.0.0.1:{port}", logPath, mdServer, $"tcp://127.0.0.1:{mdPort}");
    }

    public void Dispose()
    {
        try { Server.Dispose(); } catch { }
        try { _mdServer?.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
        Console.WriteLine($"[CTP-Front] 本测试服务端日志: {LogPath}");
    }
}
