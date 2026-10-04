using System;
using System.IO;
using Acme.Ctp.Sim;
using Acme.Ctp.Sim.Server;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 端点解析与 host 侧选项测试：<see cref="SimAddress"/> 解析矩阵（默认端口 / host:port / 垃圾值）、
/// <see cref="SimHostOptions"/> 与 <see cref="SimOptions"/> 的环境变量读取（AETHER_CTP_SIM_* 全家）、上市表 env 解析。
/// <para>
/// （2026-10-04：原「ICtpSession 直连官方 SDK 链路」DI 组合测试测的是**客户端**组合根，
/// 已迁至 <c>tests/Acme.Ctp.Infrastructure.Tests/CtpSessionRegistrationTests</c>——那不是柜台的事。）
/// </para>
/// </summary>
public class RoutingSessionTests
{

    // —— 地址判据 ——

    [Theory]
    [InlineData("sim", true)]                 // 保留字（大小写不敏感）
    [InlineData("SIM", true)]
    [InlineData("  sim ", true)]              // 容忍空白
    [InlineData("127.0.0.1:17800", true)]     // 默认仿真端点直写
    [InlineData("tcp://127.0.0.1:17800", true)]
    [InlineData("tcp://192.168.1.10:17801", true)]     // 远程仿真前置：任意 host 的交易腿端口（服务器部署形态）
    [InlineData("server.example.com:17801", true)]
    [InlineData("192.168.1.10:17801", true)]           // 无 tcp:// 前缀同判
    [InlineData("tcp://192.168.1.10:17802", false)]    // 行情腿端口不算交易前置
    [InlineData("tcp://192.168.1.10:17800", false)]    // JSON-RPC 端口的远程形态不判 sim（只认本地直写）
    [InlineData("tcp://server:10130", false)]          // 真实前置端口
    [InlineData("tcp://127.0.0.1:10041", false)]  // 其它端口的本地地址不是仿真
    [InlineData("tcp://180.168.146.187:10131", false)]
    [InlineData("simnow", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 前置地址判据区分仿真与真实(string? front, bool expected)
    {
        Assert.Equal(expected, SimAddress.IsSimFront(front));
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
