using System;
using System.IO;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.Ctp.Infrastructure.Sim;
using Acme.Ctp.Sim;
using Acme.Ctp.Sim.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 「用前置地址选柜台」的路由测试（取代 Phase 1 的环境变量切换）：
/// <see cref="RoutingCtpSession"/> 是唯一 <see cref="ICtpSession"/> 注册，前置地址填 <c>sim</c> ⇒
/// 仿真后端（连独立 host），填真实前置 ⇒ 官方链路。无环境变量、无重启、无 UI/VM 改动。
/// <para>含端点解析矩阵与 host 侧选项（<see cref="SimHostOptions"/>）测试。</para>
/// </summary>
public class RoutingSessionTests
{
    /// <summary>临时目录下的 ctp.db 路径——**绝不**让测试碰 <c>~/.aether/ctp.db</c>（真实用户的配置库）。</summary>
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"aether-ctp-sim-test-{Guid.NewGuid():N}", "ctp.db");

    private static CtpLoginRequest LoginRequest(string frontAddress, string userId = SimHarness.Account) => new()
    {
        FrontAddress = frontAddress,
        MdFrontAddress = "tcp://127.0.0.1:40002",
        BrokerId = SimHarness.Broker,
        UserId = userId,
        Password = "任意",
        AppId = "aether",
        AuthCode = "auth",
    };

    // —— 地址判据 ——

    [Theory]
    [InlineData("sim", true)]                 // 保留字（大小写不敏感）
    [InlineData("SIM", true)]
    [InlineData("  sim ", true)]              // 容忍空白
    [InlineData("127.0.0.1:17800", true)]     // 默认仿真端点直写
    [InlineData("tcp://127.0.0.1:17800", true)]
    [InlineData("tcp://127.0.0.1:10041", false)]  // 其它端口的本地地址不是仿真
    [InlineData("tcp://180.168.146.187:10131", false)]
    [InlineData("simnow", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 前置地址判据区分仿真与真实(string? front, bool expected)
    {
        Assert.Equal(expected, SimAddress.IsSimFront(front));
    }

    // —— DI 组合 ——

    [Fact]
    public void ICtpSession直连官方SDK链路_路由会话已退役出DI()
    {
        using var provider = new ServiceCollection().AddCtpInfrastructure(TempDbPath()).BuildServiceProvider();

        // 2026-10-01 用户定调：桌面端砍掉 sim 关键字直连——ICtpSession 直连 CtpSessionService
        //（本地前置 tcp://127.0.0.1:17801/17802 或真实 SimNow 同链路；RoutingCtpSession 不再进 DI）
        var session = provider.GetRequiredService<ICtpSession>();
        var real = Assert.IsType<CtpSessionService>(session);
        Assert.Same(real, provider.GetRequiredService<CtpSessionService>());   // 同一单例
        Assert.False(session.IsConnected);
        Assert.Null(real.LoginResult);
    }

    // —— 端点解析（两端共用） ——

    [Fact]
    public void 地址解析支持默认端口与hostport格式()
    {
        var (host, port) = SimAddress.Parse(null);
        Assert.Equal(SimEndpointDefaults.Host, host);
        Assert.Equal(SimEndpointDefaults.Port, port);

        (host, port) = SimAddress.Parse("19000");
        Assert.Equal(SimEndpointDefaults.Host, host);
        Assert.Equal(19000, port);

        (host, port) = SimAddress.Parse("127.0.0.1:19000");
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(19000, port);

        (host, port) = SimAddress.Parse("localhost:19000 ");
        Assert.Equal("localhost", host);
        Assert.Equal(19000, port);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("99999999")]
    [InlineData(":17800")]
    [InlineData("a:b:c")]
    public void 地址格式垃圾值解析即抛(string bad)
    {
        Assert.ThrowsAny<FormatException>(() => SimAddress.Parse(bad));
    }

    // —— host 侧选项 ——

    [Fact]
    public void 仿真宿主选项从环境变量读取端点与库路径()
    {
        var savedAddr = Environment.GetEnvironmentVariable(SimAddress.EnvVar);
        var savedDb = Environment.GetEnvironmentVariable(SimHostOptions.DbEnvVar);
        Environment.SetEnvironmentVariable(SimAddress.EnvVar, "127.0.0.1:19001");
        Environment.SetEnvironmentVariable(SimHostOptions.DbEnvVar, "/tmp/host-ctp-sim.db");
        try
        {
            var opts = SimHostOptions.FromEnvironment();

            Assert.Equal("127.0.0.1", opts.Host);
            Assert.Equal(19001, opts.Port);
            Assert.Equal("/tmp/host-ctp-sim.db", opts.Core.DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SimAddress.EnvVar, savedAddr);
            Environment.SetEnvironmentVariable(SimHostOptions.DbEnvVar, savedDb);
        }
    }

    [Fact]
    public void 仿真宿主默认库路径在用户目录下与真实配置库分文件()
    {
        // 真实库里存的是**明文 CTP 密码**：仿真账本与它必须分文件——独立 host 形态下默认
        // ~/.aether/ctp-sim.db（与客户端 ctp.db 同目录、不同文件）
        var simDb = SimHostOptions.DefaultDatabasePath();

        Assert.EndsWith("ctp-sim.db", simDb);
        Assert.EndsWith(Path.Combine(".aether", "ctp-sim.db"), simDb);
        Assert.NotEqual(Path.Combine(Path.GetDirectoryName(simDb)!, "ctp.db"), simDb);
    }

    [Fact]
    public void 仿真选项从环境变量读取覆盖值()
    {
        // host 侧语义（AETHER_CTP_SIM_TICK_MS/SEED/BALANCE 全部由 host 进程读取）
        var names = new[] { "AETHER_CTP_SIM_TICK_MS", "AETHER_CTP_SIM_SEED", "AETHER_CTP_SIM_BALANCE" };
        var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_TICK_MS", "0");
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_SEED", "4242");
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_BALANCE", "500000");

            var options = SimOptions.FromEnvironment("/tmp/ctp-sim.db");

            Assert.Equal("/tmp/ctp-sim.db", options.DatabasePath);
            Assert.Equal(0, options.TickIntervalMs);        // 0 ⇒ host 不起自动行情喂送
            Assert.Equal(4242, options.Seed);
            Assert.Equal(500_000, options.InitialBalance, 6);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
                Environment.SetEnvironmentVariable(names[i], saved[i]);
        }
    }

    [Fact]
    public void 环境变量缺省时取内置默认值()
    {
        foreach (var name in new[] { "AETHER_CTP_SIM_TICK_MS", "AETHER_CTP_SIM_SEED", "AETHER_CTP_SIM_BALANCE", "AETHER_CTP_SIM_BOARD", "AETHER_CTP_SIM_MODE", "AETHER_CTP_SIM_REPLAY" })
            Environment.SetEnvironmentVariable(name, null);

        var options = SimOptions.FromEnvironment(null);

        Assert.Null(options.DatabasePath);
        Assert.Equal(500, options.TickIntervalMs);
        Assert.Equal(20260925, options.Seed);
        Assert.Equal(1_000_000, options.InitialBalance, 6);
        Assert.Null(options.ListedInstruments);   // 上市表空 ⇒ 核心 InitializeAsync 回落默认 zx001
        Assert.Equal(SimMarketMode.Exchange, options.MarketMode);   // 默认 = 交易所模式（2026-09-28 用户定调）

        var host = SimHostOptions.FromEnvironment();
        Assert.Equal(SimMarketMode.Exchange, host.Core.MarketMode);
    }

    [Fact]
    public void 价格源模式环境变量解析()
    {
        var names = new[] { "AETHER_CTP_SIM_MODE", "AETHER_CTP_SIM_REPLAY" };
        var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_REPLAY", null);
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", "walk");
            Assert.Equal(SimMarketMode.Walk, SimOptions.FromEnvironment(null).MarketMode);

            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", "Exchange");   // 大小写不敏感
            Assert.Equal(SimMarketMode.Exchange, SimOptions.FromEnvironment(null).MarketMode);

            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", "  ");        // 空白 ⇒ 默认交易所
            Assert.Equal(SimMarketMode.Exchange, SimOptions.FromEnvironment(null).MarketMode);

            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", "wolk");      // 坏值 ⇒ 拒（打错字宁可知晓）
            Assert.Throws<ArgumentException>(() => SimOptions.FromEnvironment(null));
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
                Environment.SetEnvironmentVariable(names[i], saved[i]);
        }
    }

    [Fact]
    public void 回放设定强制归一walk模式()
    {
        var names = new[] { "AETHER_CTP_SIM_MODE", "AETHER_CTP_SIM_REPLAY" };
        var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            // REPLAY 设定（MODE 未设 ⇒ 默认 exchange）⇒ SimHostOptions 归一回 walk——回放骑 walk 撮合机制
            //（InjectQuote 在交易所模式 no-op，不归一回放会静默失效）
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", null);
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_REPLAY", "/tmp/ticks.csv");
            Assert.Equal(SimMarketMode.Walk, SimHostOptions.FromEnvironment().Core.MarketMode);
            Assert.Equal(SimMarketMode.Exchange, SimOptions.FromEnvironment(null).MarketMode);   // 归一只发生在 host 层

            // 显式 walk + REPLAY ⇒ 本来就是 walk
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_MODE", "walk");
            Assert.Equal(SimMarketMode.Walk, SimHostOptions.FromEnvironment().Core.MarketMode);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
                Environment.SetEnvironmentVariable(names[i], saved[i]);
        }
    }

    [Fact]
    public void 上市合约表环境变量解析()
    {
        var names = new[] { "AETHER_CTP_SIM_BOARD" };
        var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_BOARD", " ag2610 , IF2610 ");
            var o = SimOptions.FromEnvironment(null);
            Assert.Equal(new[] { "ag2610", "IF2610" }, o.ListedInstruments);   // 去空格、弃空项

            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_BOARD", "   ");
            Assert.Null(SimOptions.FromEnvironment(null).ListedInstruments);   // 坏值容忍回默认（不拒启动）
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
                Environment.SetEnvironmentVariable(names[i], saved[i]);
        }
    }
}
