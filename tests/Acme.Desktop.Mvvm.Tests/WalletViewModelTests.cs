using Acme.Desktop.Mvvm;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// 钱包页 VM（2026-10-08）：种子生命周期（创建/导入/损坏）+ 表单校验（先于网络——无需节点）+ 提币端口。
/// 链上转账/充值/余额的协议路径已由 tests/Aether.Ledger.Tests 全栈覆盖；此处钉 VM 的本地行为。
/// </summary>
public class WalletViewModelTests
{
    private sealed record InMemoryPrefs : IPreferencesStore
    {
        public Dictionary<string, string?> Values { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        { Values[key] = value; return Task.CompletedTask; }
    }

    private sealed class StubExchangeClient : ILedgerExchangeClient
    {
        public string? FailWith;
        public Task<string> WithdrawAsync(string exchangeEndpoint, string userId, string password,
            string toAddressHex, ulong amountAtoms, CancellationToken ct = default)
            => FailWith is { } msg
                ? Task.FromException<string>(new InvalidOperationException(msg))
                : Task.FromResult("abc123def456abc123def456abc123def456abc123def456abc123def456abcd");
    }

    private static (WalletViewModel Vm, InMemoryPrefs Prefs, StubExchangeClient Exchange) NewVm(string seedPath)
    {
        var prefs = new InMemoryPrefs();
        var exchange = new StubExchangeClient();
        var sp = new ServiceCollection()
            .AddSingleton<IPreferencesStore>(prefs)
            .AddSingleton<ILedgerExchangeClient>(exchange)
            .AddSingleton(new LedgerWalletLocation(seedPath))
            .BuildServiceProvider();
        return (new WalletViewModel(sp), prefs, exchange);
    }

    private static string TempSeedPath()
        => Path.Combine(Path.GetTempPath(), $"wallet-test-{Guid.NewGuid():N}", "agent-wallet.seed");

    [Fact]
    public async Task 创建钱包_需确认备份_种子落盘_地址显示()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, _) = NewVm(path);
            Assert.False(vm.HasWallet);

            vm.StartCreateCommand.Execute(null);
            Assert.True(vm.IsCreating);
            Assert.NotNull(vm.NewSeedHex);
            Assert.Equal(64, vm.NewSeedHex!.Length);
            var seedHex = vm.NewSeedHex;   // 保存成功后会被清空（种子只展示一次）——先捕获

            // 未勾「已备份」不可保存（命令守卫）
            vm.ConfirmCreateCommand.Execute(null);
            Assert.False(vm.HasWallet);

            vm.SeedBackedUp = true;
            await vm.ConfirmCreateCommand.ExecuteAsync(null);

            Assert.True(vm.HasWallet);
            Assert.NotNull(vm.Address);
            Assert.Equal(40, vm.Address!.Length);
            Assert.True(File.Exists(path));
            Assert.Contains(seedHex, File.ReadAllText(path));   // 只此一次展示的种子与落盘一致
            Assert.Null(vm.NewSeedHex);   // 展示即清——此后 UI 不再显示种子
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task 导入钱包_合法种子_落盘生效()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, _) = NewVm(path);
            vm.StartImportCommand.Execute(null);
            vm.ImportSeedText = new string('a', 64);   // 64 hex = 32 字节（奇偶不限——FromHexString 容忍）
            await vm.ImportCommand.ExecuteAsync(null);

            Assert.True(vm.HasWallet);
            Assert.False(vm.IsImporting);
            Assert.True(File.Exists(path));
        }
        finally { TryCleanup(path); }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zz" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]   // 非 hex 字符
    public async Task 导入钱包_非法种子_报错不落盘(string bad)
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, _) = NewVm(path);
            vm.StartImportCommand.Execute(null);
            vm.ImportSeedText = bad;
            await vm.ImportCommand.ExecuteAsync(null);

            Assert.False(vm.HasWallet);
            Assert.True(vm.IsImporting);   // 失败保留导入面板（错误可见、用户可直接改种子重试）
            Assert.NotNull(vm.ErrorText);
            Assert.Contains("种子格式非法", vm.ErrorText);
            Assert.False(File.Exists(path));
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task 转账_金额与地址先校验_零网络()
    {
        // 校验发生在建任何链连接之前——本测试无需节点（坏输入 → 本地错误，不抛网络异常）
        var path = TempSeedPath();
        try
        {
            var (vm, _, _) = NewVm(path);
            vm.StartCreateCommand.Execute(null);
            vm.SeedBackedUp = true;
            await vm.ConfirmCreateCommand.ExecuteAsync(null);

            vm.TransferTo = new string('b', 40);
            vm.TransferAmount = "-1";
            await vm.TransferCommand.ExecuteAsync(null);
            Assert.Contains("币数非法", vm.ErrorText);

            vm.TransferAmount = "1";
            vm.TransferTo = "not-an-address";
            await vm.TransferCommand.ExecuteAsync(null);
            Assert.NotNull(vm.ErrorText);   // 地址格式非法（LedgerAddress.Parse 本地抛）
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task 充值_未配custody_给指引()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, _) = NewVm(path);
            vm.StartCreateCommand.Execute(null);
            vm.SeedBackedUp = true;
            await vm.ConfirmCreateCommand.ExecuteAsync(null);

            vm.DepositAccount = "u1";
            vm.DepositAmount = "5";
            await vm.DepositCommand.ExecuteAsync(null);
            Assert.NotNull(vm.ErrorText);
            Assert.Contains("custody", vm.ErrorText);
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task 提币_经端口客户端_成功回显tx哈希()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, exchange) = NewVm(path);
            vm.StartCreateCommand.Execute(null);
            vm.SeedBackedUp = true;
            await vm.ConfirmCreateCommand.ExecuteAsync(null);

            vm.WithdrawUser = "u1";
            vm.WithdrawPassword = "pw";
            vm.WithdrawAmount = "2";   // 提币地址留空 → 默认本钱包地址
            await vm.WithdrawCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorText);
            Assert.NotNull(vm.StatusText);
            Assert.Contains("提币已提交", vm.StatusText);
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task 提币_端口失败_错误透传()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, _, exchange) = NewVm(path);
            exchange.FailWith = "venue 拒绝提币：现货提币量超持仓";
            vm.StartCreateCommand.Execute(null);
            vm.SeedBackedUp = true;
            await vm.ConfirmCreateCommand.ExecuteAsync(null);

            vm.WithdrawUser = "u1";
            vm.WithdrawPassword = "pw";
            vm.WithdrawAmount = "999";
            await vm.WithdrawCommand.ExecuteAsync(null);

            Assert.Contains("现货提币量超持仓", vm.ErrorText);
        }
        finally { TryCleanup(path); }
    }

    [Fact]
    public async Task LoadAsync_设置持久化恢复()
    {
        var path = TempSeedPath();
        try
        {
            var (vm, prefs, _) = NewVm(path);
            prefs.Values["wallet.node"] = "10.0.0.5:17805";
            prefs.Values["wallet.custody"] = new string('c', 40);
            prefs.Values["wallet.account"] = "u1";

            await vm.LoadAsync();

            Assert.Equal("10.0.0.5:17805", vm.NodeAddress);
            Assert.Equal(new string('c', 40), vm.CustodyAddress);
            Assert.Equal("u1", vm.DepositAccount);
            Assert.Equal("u1", vm.WithdrawUser);   // 提币账号带出充值账号
        }
        finally { TryCleanup(path); }
    }

    private static void TryCleanup(string seedPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(seedPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }
}
