using Acme.Ctp.Domain;
using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;
using Acme.Ctp.Simulator.Storage;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 账号系统（自助开户 + 密码认证 + 防爆破，**密码必填**）：首次登录 = 开户（密码入档 PBKDF2 哈希）；
/// 空/空白密码一律拒 90017（开新户也不例外——账户从出生即受保护）；已设密码的账户密码必须匹配；
/// 存量无密码账户（升级前的库）带密首登 = 补设（无密码账户唯一的存在来源）。
/// 字段对齐真实 CTP 登录三件（BrokerId/UserId/Password——wire <see cref="LoginArg"/>）。
/// </summary>
public class AccountAuthTests
{
    private static SimOptions AuthOptions(int maxFailures = 5, int lockoutSec = 60) => new()
    {
        TickIntervalMs = 0,
        DatabasePath = null,
        MarketMode = SimMarketMode.Walk,   // 认证与模式无关——显式 pin 与其余 walk 用例一致
        PasswordIterations = 1000,   // 测试提速（仅迭代数——哈希格式/验证不受影响）
        LoginMaxFailures = maxFailures,
        LoginLockoutSeconds = lockoutSec,
    };

    private static async Task<SimTradingCore> NewAuthCoreAsync(SimOptions? options = null)
        => await SimHarness.NewCoreAsync(options ?? AuthOptions(), injectQuote: false);

    [Fact]
    public async Task 首次登录带密码即开户设密()
    {
        var core = await NewAuthCoreAsync();

        core.Login("9999", "alice", "pw123");   // 首登 = 开户 + 设密
        core.Login("9999", "alice", "pw123");   // 正确密码可再登

        var acc = core.QueryAccounts().Single(a => a.AccountId == "alice");
        Assert.True(acc.HasPassword);
        Assert.Equal("9999", acc.BrokerId);   // 经纪商记为元数据
        Assert.Equal(SimHarness.InitialBalance, acc.Balance, 6);
    }

    [Fact]
    public async Task 密码错误拒90017_空密码对已设密账户同样拒()
    {
        var core = await NewAuthCoreAsync();
        core.Login("9999", "alice", "pw123");

        var ex = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "alice", "wrong"));
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);

        var ex2 = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "alice"));
        Assert.Equal(SimErrorCodes.BadCredentials, ex2.ErrorId);
    }

    [Fact]
    public async Task 空密码登录一律被拒_开新户也不例外()
    {
        var core = await NewAuthCoreAsync();

        var ex = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "nancy"));
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);
        Assert.Contains("密码不能为空", ex.ErrorMsg);
        Assert.Empty(core.QueryAccounts());   // 未开户——密码必填拦在开户之前
    }

    [Fact]
    public async Task 存量无密码账户带密首登补设()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sim-legacy-{Guid.NewGuid():N}.db");
        try
        {
            // 直接经 store 注入一个无密码的存量账户（升级前旧库的形态——无密码账户唯一的存在来源）
            var store = SimStore.ForFile(db);
            await store.InitializeAsync();
            await store.SaveAccountAsync(new SimAccountSnapshot("legacy", 500_000, 0, 0, 0, 0, 0, 0));

            var core = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await core.InitializeAsync();   // 载入 legacy（无密码）
            Assert.False(core.QueryAccounts().Single(a => a.AccountId == "legacy").HasPassword);

            core.Login("9999", "legacy", "new-pw");   // 带密首登 = 补设（迁移路径）
            Assert.True(core.QueryAccounts().Single(a => a.AccountId == "legacy").HasPassword);
            Assert.Equal(500_000, core.QueryAccount("legacy").Balance, 6);   // 余额是原值，不是 InitialBalance

            var ex = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "legacy", "bad"));
            Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);   // 此后凭据受保护
        }
        finally { try { File.Delete(db); } catch { } }
    }

    [Fact]
    public async Task 防爆破锁定与到期解锁()
    {
        var now = SimHarness.TradingNow;   // 可变闭包 ⇒ 时钟可推进（SimClock 捕获变量引用）
        var core = new SimTradingCore(AuthOptions(maxFailures: 3, lockoutSec: 60), new SimClock(() => now));
        await core.InitializeAsync();
        core.Login("9999", "carol", "pw");

        for (var i = 0; i < 3; i++)   // 连续 3 次失败 ⇒ 锁定
            Assert.Throws<CtpBusinessException>(() => core.Login("9999", "carol", $"bad{i}"));

        var locked = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "carol", "pw"));   // 锁定期内正确密码也拒
        Assert.Equal(SimErrorCodes.BadCredentials, locked.ErrorId);
        Assert.Contains("锁定", locked.ErrorMsg);

        now = now.AddSeconds(61);      // 时钟推进 ⇒ 解锁
        core.Login("9999", "carol", "pw");
    }

    [Fact]
    public async Task 凭据失败连接不绑定账户()
    {
        using var sim = LoopbackSim.Start(o => o with { PasswordIterations = 1000 });
        sim.Core.Login(SimHarness.Broker, "dave", "pw");   // 服务端侧直接开户设密（余额 1e6）

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => sim.Session.LoginAsync(LoginReq("dave", "bad")));
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);

        // 连接未绑上 dave：查询走未登录本地空账（契约④），不是 dave 的 1e6
        var account = await sim.Session.QueryAccountAsync();
        Assert.Equal(0, account.Balance, 6);
    }

    [Fact]
    public async Task 密码跨线E2E_首登设密错拒90017正确可再登()
    {
        using var sim = LoopbackSim.Start(o => o with { PasswordIterations = 1000 });

        await sim.Session.LoginAsync(LoginReq("erin", "pw123"));   // 首登开户设密（密码过线）

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => sim.Session.LoginAsync(LoginReq("erin", "nope")));
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);

        await sim.Session.LoginAsync(LoginReq("erin", "pw123"));   // 正确密码仍可登
        var account = await sim.Session.QueryAccountAsync();
        Assert.Equal(SimHarness.InitialBalance, account.Balance, 6);   // 登录态有效——查到自己的账
    }

    [Fact]
    public async Task 密码落盘重启续用()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sim-auth-{Guid.NewGuid():N}.db");
        try
        {
            var core = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await core.InitializeAsync();
            core.Login("9999", "frank", "pw");
            core.Dispose();   // 同步 flush

            var revived = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await revived.InitializeAsync();
            var ex = Assert.Throws<CtpBusinessException>(() => revived.Login("9999", "frank", "bad"));
            Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);
            revived.Login("9999", "frank", "pw");
            Assert.True(revived.QueryAccounts().Single(a => a.AccountId == "frank").HasPassword);
        }
        finally { try { File.Delete(db); } catch { } }
    }

    private static CtpLoginRequest LoginReq(string userId, string password) => new()
    {
        FrontAddress = "sim",
        MdFrontAddress = "",
        BrokerId = SimHarness.Broker,
        UserId = userId,
        Password = password,
        AppId = "aether",
        AuthCode = "auth",
    };
}
