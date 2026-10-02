// 最小直连测试：官方 SDK → CTP 前置（无代理，无记录——纯验证握手/登录链路）。
// 由 Program.cs 分发进入：CLI 首参以 tcp: 开头即本模式——
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <account> <password> [idleSec]
// 第四参 = 登录后空转秒数（默认 0 不空转）：空转期间每 30s 查一次资金 + 打印断线事件——
// 复现「长连接被掐」类问题（2026-10-02 实测：服务端不应答客户端心跳 ⇒ ~90-150s 后 native SDK 断线）。
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.ProtoCapture;

internal static class DirectTest
{
    public static async Task RunAsync(string front, string user, string pwd, int idleSec = 0)
    {
        Console.WriteLine($"[Test] 直连 {front} user={user} idle={idleSec}s");

        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-direct-test");
        Directory.CreateDirectory(flowDir);

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
                FrontAddress = front,
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

            Console.WriteLine("[Test] QueryInvestorPosition…");
            var positions = await trader.QueryInvestorPositionAsync("9999", user, ct: CancellationToken.None);
            Console.WriteLine($"[Test] ✓ 持仓 {positions.Count} 条");

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
    }
}
