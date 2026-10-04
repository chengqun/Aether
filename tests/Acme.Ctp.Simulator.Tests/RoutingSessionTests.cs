using System;
using System.IO;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.Ctp.Sim;
using Acme.Ctp.Sim.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 「用前置地址选柜台」的组合与选项测试（取代 Phase 1 的环境变量切换）：
/// 2026-10-01 收口后 <see cref="ICtpSession"/> 直连 <see cref="Acme.Ctp.Infrastructure.CtpSessionService"/>
/// （官方 SDK 链路——本地前置 tcp://127.0.0.1:17801/17802 与真实 SimNow 同形）；旧 <c>sim</c> 关键字
/// JSON-RPC 路由（RoutingCtpSession）已于 2026-10-04 删除。
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
        // host 侧语义（AETHER_CTP_SIM_BALANCE/BOARD 全部由 host 进程读取）
        var names = new[] { "AETHER_CTP_SIM_BALANCE" };
        var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("AETHER_CTP_SIM_BALANCE", "500000");

            var options = SimOptions.FromEnvironment("/tmp/ctp-sim.db");

            Assert.Equal("/tmp/ctp-sim.db", options.DatabasePath);
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
        foreach (var name in new[] { "AETHER_CTP_SIM_BALANCE", "AETHER_CTP_SIM_BOARD" })
            Environment.SetEnvironmentVariable(name, null);

        var options = SimOptions.FromEnvironment(null);

        Assert.Null(options.DatabasePath);
        Assert.Equal(1_000_000, options.InitialBalance, 6);
        Assert.Null(options.ListedInstruments);   // 上市表空 ⇒ 核心 InitializeAsync 回落默认 zx001
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
