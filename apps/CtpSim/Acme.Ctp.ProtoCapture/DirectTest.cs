// 最小直连测试：官方 SDK → CTP 前置（无代理，无记录——纯验证握手/登录链路）。
// 由 Program.cs 分发进入：CLI 首参以 tcp: 开头即本模式——
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <account> <password> [idleSec]
// 第四参 = 登录后空转秒数（默认 0 不空转）：空转期间每 30s 查一次资金 + 打印断线事件——
// 复现「长连接被掐」类问题（2026-10-02 实测：服务端不应答客户端心跳 ⇒ ~90-150s 后 native SDK 断线）。
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.ProtoCapture;

internal static class DirectTest
{
    public static async Task RunAsync(string front, string user, string pwd, int idleSec = 0, bool proxy = false, int soak = 0)
    {
        Console.WriteLine($"[Test] 直连 {front} user={user} idle={idleSec}s proxy={proxy}");

        // 每次独立流目录（复用会重放旧流 ⇒ 真前置拒收「不合法的登录」——2026-10-15 实测踩中）
        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-direct-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flowDir);

        // 代理录制模式：官方 SDK → RecordingProxy(17001) → front——线级对比「真实前置 vs 本地前置」
        // 在 idle+周期查询下的两侧字节流（2026-10-03 心跳/停摆排查）。
        RecordingProxy? tradeProxy = null;
        var connectFront = front;
        if (proxy)
        {
            var captureDir = Path.Combine(Path.GetTempPath(), "ctp-proto-capture", $"direct-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(captureDir);
            tradeProxy = new RecordingProxy(17001, front, "Trade", Path.Combine(captureDir, "trade.txt"));
            tradeProxy.Start();
            connectFront = "tcp://127.0.0.1:17001";
            Console.WriteLine($"[Test] 录制目录: {captureDir}");
        }

        using var trader = new CtpTrader(flowDir);
        trader.FrontConnected += (_, _) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✓ 前置已连接");
        trader.FrontDisconnected += (_, r) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✗ 前置断开: reason={r}");
        trader.RspError += (_, e) => Console.WriteLine($"[Test] 错误: [{e.ErrorId}] {e.ErrorMsg}");
        trader.OrderReceived += (_, o) => Console.WriteLine($"[Test] 委托: {o.InstrumentId} status={(char)o.OrderStatusRaw}");
        trader.TradeReceived += (_, t) => Console.WriteLine($"[Test] 成交: {t.InstrumentId} {t.Volume}@{t.Price}");

        try
        {
            Console.WriteLine("[Test] LoginAsync…");
            var result = await trader.LoginAsync(new CtpConnectRequest
            {
                FrontAddress = connectFront,
                BrokerId = "9999",
                UserId = user,
                Password = pwd,
                AppId = "simnow_client_test",
                AuthCode = "0000000000000000",
            }, CancellationToken.None);
            Console.WriteLine($"[Test] ✓ 登录成功: 交易日={result.TradingDay} FrontID={result.FrontID} MaxOrderRef={result.MaxOrderRef}");

            Console.WriteLine("[Test] ConfirmSettlement…");
            await trader.ConfirmSettlementAsync("9999", user, CancellationToken.None);
            Console.WriteLine("[Test] ✓ 结算确认完成");

            Console.WriteLine("[Test] QueryTradingAccount…");
            var account = await trader.QueryTradingAccountAsync("9999", user, CancellationToken.None);
            Console.WriteLine($"[Test] ✓ 资金: Balance={account.Balance} Available={account.Available}");

            // 启动查询容错（真前置限频 ~1s/同型——背靠背发会被限流超时；soak 循环才是本体，
            // 单个启动查询超时不该终止 run。间隔 1.2s 尽量避开限频窗）
            try
            {
                await Task.Delay(1200);
                Console.WriteLine("[Test] QueryInvestorPosition…");
                var positions = await trader.QueryInvestorPositionAsync("9999", user, ct: CancellationToken.None);
                Console.WriteLine($"[Test] ✓ 持仓 {positions.Count} 条");
            }
            catch (Exception ex) { Console.WriteLine($"[Test] 启动持仓查询超时（限频，继续）: {ex.Message}"); }
            try
            {
                await Task.Delay(1200);
                Console.WriteLine("[Test] QueryOrder（过滤 au2610——预期空，首查）…");
                var filtered = await trader.QueryOrderAsync("9999", user, "au2610", CancellationToken.None);
                Console.WriteLine($"[Test] ✓ 过滤报单 {filtered.Count} 条");
                Console.WriteLine("[Test] QueryOrder…");
                var orders = await trader.QueryOrderAsync("9999", user, ct: CancellationToken.None);
                Console.WriteLine($"[Test] ✓ 报单 {orders.Count} 条（首条: {(orders.Count > 0 ? orders[0].ToString() : "无")}）");
            }
            catch (Exception ex) { Console.WriteLine($"[Test] 启动报单查询超时（限频，继续）: {ex.Message}"); }

            // Soak 模式（2026-10-15 ≥256 计数形态取证）：连打 N 笔持仓查询（真前置限频 ~1s/同型查询，
            // 350ms 间隔 + 失败退避），proxy 录制两侧字节——第 257+ 笔的 S→C 应答 = 客户端接受的 ≥256
            // instance 编码的地表真值（本地前置四候选形态全被拒后停止猜测、直取证据）。
            for (var i = 1; i <= soak; i++)
            {
                try
                {
                    var p = await trader.QueryInvestorPositionAsync("9999", user, ct: CancellationToken.None);
                    if (i % 25 == 0 || i >= 250) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] soak#{i} ✓ 持仓 {p.Count} 条");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] soak#{i} ✗ {ex.Message}（退避 1.5s）");
                    await Task.Delay(1500);
                }
                await Task.Delay(350);
            }
            if (soak > 0) Console.WriteLine($"[Test] soak 完成 {soak} 笔");

            // 空转观测：每 30s 一次轻查询（应答活着 = 心跳/连接健康；超时 = 被掐的指征）
            var deadline = Environment.TickCount64 + idleSec * 1000L;
            var n = 0;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(Math.Min(30_000, (int)(deadline - Environment.TickCount64)));
                if (Environment.TickCount64 >= deadline) break;
                n++;
                try
                {
                    var a = await trader.QueryTradingAccountAsync("9999", user, CancellationToken.None);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] idle#{n} ✓ 查询应答 Balance={a.Balance}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] idle#{n} ✗ 查询失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Test] ✗ 异常: {ex.Message}");
        }

        Console.WriteLine("[Test] 完成");
        if (tradeProxy is not null)
        {
            await Task.Delay(500);   // 排空代理缓冲
            tradeProxy.Dispose();
        }
    }
}
