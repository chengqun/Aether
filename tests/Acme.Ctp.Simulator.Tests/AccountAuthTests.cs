using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
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
        DatabasePath = null,
        PasswordIterations = 1000,   // 测试提速（仅迭代数——哈希格式/验证不受影响）
        LoginMaxFailures = maxFailures,
        LoginLockoutSeconds = lockoutSec,
    };

    private static async Task<SimTradingCore> NewAuthCoreAsync(SimOptions? options = null)
        => await SimHarness.NewCoreAsync(options ?? AuthOptions(), seedMaker: false);   // 认证用例不下单——无须流动性

    [Fact]
    public async Task 首次登录带密码即开户设密()
    {
        var core = await NewAuthCoreAsync();

        core.Login("9999", "alice", "pw123");   // 首登 = 开户 + 设密
        core.Login("9999", "alice", "pw123");   // 正确密码可再登

        var acc = core.QueryAccounts().Single(a => a.AccountId == "alice");
        Assert.True(acc.HasPassword);
        Assert.Equal("9999", acc.BrokerId);   // 经纪商记为元数据
        Assert.Equal(0, acc.Balance, 6);   // 比特币模型（2026-10-08）：首登开户零余额——水龙头默认关，资金只能从卖币进来
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

    // —— 双密码域（2026-10-09）：前置域（挑战响应密文 hex）与 JSON-RPC 明文域各自独立设密/补设——
    //    修前单列互斥：经前置开的户，钱包页提币发明文永远 90017（闭环断在最后一焊）。

    [Fact]
    public async Task 双密码域_前置开户后明文域首登补设_两扇门都通()
    {
        var core = await NewAuthCoreAsync();
        const string cipherHex = "a1b2c3d4e5f60718";   // 前置传入的挑战响应密文（核心视为不透明字符串）

        core.Login("9999", "u1", cipherHex, frontDomain: true);   // 期货页（前置）首登 = 开户，front 域设密；明文域留空
        core.Login("9999", "u1", cipherHex, frontDomain: true);   // front 域正确密文可再登
        core.Login("9999", "u1", "plain-pw");                     // 钱包页提币（JSON-RPC 明文）首登 = 该域补设
        core.Login("9999", "u1", "plain-pw");                     // 明文域正确可再登
        core.Login("9999", "u1", cipherHex, frontDomain: true);   // 前置域不受明文补设影响，仍通

        // 两域各自凭据受保护（互不串域：明文密码在前置域无效，密文在明文域无效）
        Assert.Throws<CtpBusinessException>(() => core.Login("9999", "u1", "bad"));
        Assert.Throws<CtpBusinessException>(() => core.Login("9999", "u1", "bad", frontDomain: true));
    }

    [Fact]
    public async Task 双密码域_明文开户后前置首登补设_两扇门都通()
    {
        var core = await NewAuthCoreAsync();
        const string cipherHex = "deadbeef00112233";

        core.Login("9999", "u2", "plain-pw");                     // 钱包页（JSON-RPC）先开户
        core.Login("9999", "u2", cipherHex, frontDomain: true);   // 期货页（前置）首登 = front 域补设
        core.Login("9999", "u2", "plain-pw");                     // 明文域不受影响
        core.Login("9999", "u2", cipherHex, frontDomain: true);   // front 域正确可再登

        Assert.Throws<CtpBusinessException>(() => core.Login("9999", "u2", cipherHex));            // 密文在明文域无效
        Assert.Throws<CtpBusinessException>(() => core.Login("9999", "u2", "plain-pw", frontDomain: true));   // 明文在前置域无效
    }

    [Fact]
    public async Task 双密码域_防爆破计数按账户共享_跨域也锁定()
    {
        var now = SimHarness.TradingNow;
        var core = new SimTradingCore(AuthOptions(maxFailures: 3, lockoutSec: 60), new SimClock(() => now));
        await core.InitializeAsync();
        core.Login("9999", "kate", "cipher-pw", frontDomain: true);   // 前置域开户设密

        for (var i = 0; i < 3; i++)   // 前置域连续 3 次失败 ⇒ 锁定（计数按账户，不按域）
            Assert.Throws<CtpBusinessException>(() => core.Login("9999", "kate", $"bad{i}", frontDomain: true));

        var locked = Assert.Throws<CtpBusinessException>(() => core.Login("9999", "kate", "any"));   // 锁定期内明文域也被拒
        Assert.Contains("锁定", locked.ErrorMsg);

        now = now.AddSeconds(61);
        core.Login("9999", "kate", "plain-pw");                     // 解锁后明文域首登 = 补设
        core.Login("9999", "kate", "cipher-pw", frontDomain: true); // 前置域原密码仍通——两扇门并存
    }

    [Fact]
    public async Task 双密码域_wire级_FrontDomain标记透传到核心()
    {
        using var sim = LoopbackSim.Start(o => o with { PasswordIterations = 1000 });
        const string cipherHex = "0123456789abcdef";
        using var client = new SimRpcClient("127.0.0.1", sim.Server.LocalEndpoint!.Port);

        await client.ConnectAsync();
        // 前置形态登录（wire 携带域标记）——若标记在序列化中丢失，此登录会错写进明文域，
        // 下面的「明文域错密码拒」用例就会反向暴露（补设语义下首登不 throw，须靠后续错密码区分）
        await client.LoginAsync(new LoginArg(SimHarness.Broker, "wire-u1", cipherHex, FrontDomain: true));
        await client.LoginAsync(new LoginArg(SimHarness.Broker, "wire-u1", "plain-pw"));                     // 明文域首登 = 补设（用户提币场景，成功即闭环）
        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => client.LoginAsync(new LoginArg(SimHarness.Broker, "wire-u1", "bad")));                     // 明文域已设——错密码拒
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);
        var ex2 = await Assert.ThrowsAsync<CtpBusinessException>(
            () => client.LoginAsync(new LoginArg(SimHarness.Broker, "wire-u1", "plain-pw", FrontDomain: true)));   // 明文串在前置域无效
        Assert.Equal(SimErrorCodes.BadCredentials, ex2.ErrorId);
        await client.LoginAsync(new LoginArg(SimHarness.Broker, "wire-u1", cipherHex, FrontDomain: true));   // 前置域原密文仍通——两域并存
    }

    [Fact]
    public async Task 双密码域_两域哈希都落盘重启续用()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sim-auth2-{Guid.NewGuid():N}.db");
        try
        {
            // 两个域的设密分属两代核心：Persist 是 fire-and-forget（同一代内连续两次改哈希的
            // 快照写可乱序、旧快照后到覆盖新哈希；真实场景两次设密隔着进程/会话，无此竞态——测试按真实节奏构造）
            const string cipherHex = "cafebabe99887766";
            var core1 = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await core1.InitializeAsync();
            core1.Login("9999", "grace", cipherHex, frontDomain: true);   // 第一代：front 域开户设密
            core1.Dispose();   // 同步 flush

            var core2 = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await core2.InitializeAsync();
            Assert.Throws<CtpBusinessException>(() => core2.Login("9999", "grace", "bad", frontDomain: true));   // front 域哈希已续用（错密码拒）
            core2.Login("9999", "grace", "plain-pw");                     // 第二代：明文域首登补设
            core2.Dispose();

            var revived = new SimTradingCore(AuthOptions() with { DatabasePath = db });
            await revived.InitializeAsync();
            Assert.Throws<CtpBusinessException>(() => revived.Login("9999", "grace", "bad"));                        // 明文域错误拒（哈希续用）
            Assert.Throws<CtpBusinessException>(() => revived.Login("9999", "grace", "bad", frontDomain: true));    // front 域错误拒（哈希续用）
            revived.Login("9999", "grace", "plain-pw");                     // 两域正确均可登
            revived.Login("9999", "grace", cipherHex, frontDomain: true);
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
