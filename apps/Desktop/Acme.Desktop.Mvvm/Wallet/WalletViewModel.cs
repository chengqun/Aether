using System.Security.Cryptography;
using Aether.Ledger;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「钱包」页 ViewModel：链上钱包的用户操作面（生成/导入种子、查余额、转账、充值到交易所、从交易所提币）。
/// 用户与 agent 共用一个钱包（同一种子文件——单用户桌面；agent 工具 <c>AddLedgerTools</c> 现读同一路径）。
/// <para>种子安全：种子只在「创建钱包」时展示一次（用户确认已备份才落盘）；此后 UI 不再显示种子（导入除外）。
/// 种子文件 hex 明文落 <c>&lt;tenantDir&gt;/ledger/</c>——保管等级 = 财富本身（链上自持模型）。</para>
/// <para>设置持久化（IPreferencesStore）：节点地址 wallet.node（默认 127.0.0.1:17805——远程链经 SSH 隧道）、
/// custody 地址 wallet.custody（充值目标）、交易所地址 wallet.exchange（提币端点，默认 127.0.0.1:17800）、
/// 充值账号 wallet.account（memo 归账键——须与期货页登录账号一致）。</para>
/// UI-agnostic（零 Avalonia）。
/// </summary>
public partial class WalletViewModel : ObservableObject
{
    private readonly IPreferencesStore _prefs;
    private readonly ILedgerExchangeClient? _exchange;   // null = 未注册（测试/异常环境）→ 提币报错
    private readonly string _seedFilePath;

    // —— 钱包态 ——
    [ObservableProperty] private bool _hasWallet;
    [ObservableProperty] private string? _address;
    [ObservableProperty] private string? _balanceText;
    [ObservableProperty] private ulong _balanceAtoms;
    [ObservableProperty] private ulong _nonce;

    // —— 创建/导入流程 ——
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string? _newSeedHex;
    [ObservableProperty] private bool _seedBackedUp;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _importSeedText;

    // —— 设置（持久化；OnXChanged 落盘）——
    [ObservableProperty] private string _nodeAddress = "127.0.0.1:17805";
    [ObservableProperty] private string _custodyAddress = "";
    [ObservableProperty] private string _exchangeAddress = "127.0.0.1:17800";
    [ObservableProperty] private string _depositAccount = "";

    // —— 转账表单 ——
    [ObservableProperty] private string _transferTo = "";
    [ObservableProperty] private string _transferAmount = "";
    [ObservableProperty] private string? _transferMemo;

    // —— 充值表单（账号默认带出设置里的 depositAccount）——
    [ObservableProperty] private string _depositAmount = "";

    // —— 提币表单 ——
    [ObservableProperty] private string _withdrawUser = "";
    [ObservableProperty] private string _withdrawPassword = "";
    [ObservableProperty] private string _withdrawTo = "";
    [ObservableProperty] private string _withdrawAmount = "";

    // —— 反馈 ——
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _errorText;

    public WalletViewModel(IServiceProvider serviceProvider)
    {
        _prefs = serviceProvider.GetRequiredService<IPreferencesStore>();
        _exchange = serviceProvider.GetService<ILedgerExchangeClient>();
        _seedFilePath = serviceProvider.GetRequiredService<LedgerWalletLocation>().SeedFilePath;
    }

    /// <summary>启动加载：设置恢复 + 已有钱包载入（种子文件 → 地址）+ 余额刷新（尽力而为——节点不在不报错）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await SuppressPersistDuringAsync(async () =>
        {
            try
            {
                NodeAddress = await _prefs.GetAsync("wallet.node", ct) ?? NodeAddress;
                CustodyAddress = await _prefs.GetAsync("wallet.custody", ct) ?? "";
                ExchangeAddress = await _prefs.GetAsync("wallet.exchange", ct) ?? ExchangeAddress;
                DepositAccount = await _prefs.GetAsync("wallet.account", ct) ?? "";
            }
            catch { /* prefs 故障不阻断钱包页 */ }
        });

        LoadExistingWallet();
        if (HasWallet) await RefreshBalanceAsync(ct);
        if (!string.IsNullOrWhiteSpace(DepositAccount)) WithdrawUser = DepositAccount;
    }

    // —— 命令：钱包生命周期 ——

    [RelayCommand]
    private void StartCreate()
    {
        IsImporting = false;
        IsCreating = true;
        SeedBackedUp = false;
        NewSeedHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        ErrorText = null;
    }

    [RelayCommand]
    private async Task ConfirmCreateAsync()
    {
        if (!SeedBackedUp || NewSeedHex is not { Length: 64 }) return;
        await SaveSeedAsync(NewSeedHex);
        StatusText = "钱包已创建。⚠ 种子只此一次展示——离线备份是找回钱包的唯一途径。";
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        NewSeedHex = null;
        SeedBackedUp = false;
    }

    [RelayCommand]
    private void StartImport()
    {
        IsCreating = false;
        IsImporting = true;
        ImportSeedText = "";
        ErrorText = null;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var seed = (ImportSeedText ?? "").Trim();
        try
        {
            if (Convert.FromHexString(seed).Length != 32)
                throw new FormatException("种子须为 64 位 hex（32 字节）");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            ErrorText = $"种子格式非法：{ex.Message}";
            return;
        }
        await SaveSeedAsync(seed.ToLowerInvariant());
        StatusText = "钱包已导入。";
    }

    [RelayCommand]
    private void CancelImport() => IsImporting = false;

    // —— 命令：余额 ——

    [RelayCommand]
    private async Task RefreshBalanceAsync(CancellationToken ct = default)
    {
        if (!HasWallet || IsBusy) return;
        await RunBusyAsync(async () =>
        {
            var client = CreateNodeClient();
            var account = await client.GetBalanceAsync(Address!, ct);
            BalanceAtoms = account.BalanceAtoms;
            Nonce = account.Nonce;
            BalanceText = LedgerSupply.Format(account.BalanceAtoms);
            StatusText = $"余额已刷新（nonce {account.Nonce}）。";
        });
    }

    // —— 命令：链上转账 ——

    [RelayCommand]
    private async Task TransferAsync(CancellationToken ct = default)
    {
        if (!HasWallet || IsBusy) return;
        LedgerAddress to;
        ulong atoms;
        try
        {
            to = LedgerAddress.Parse(TransferTo.Trim());
            atoms = ParseCoins(TransferAmount);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return;
        }
        var memo = (TransferMemo ?? "").Trim();
        await RunBusyAsync(async () =>
        {
            var client = CreateNodeClient();
            var wallet = LoadWallet()!;
            var info = await client.GetInfoAsync(ct);
            var account = await client.GetBalanceAsync(wallet.Address.Hex, ct);
            if (account.BalanceAtoms < atoms)
                throw new InvalidOperationException($"余额不足：{LedgerSupply.Format(account.BalanceAtoms)} < {LedgerSupply.Format(atoms)}");
            var tx = LedgerTransaction.Sign(info.ChainId, wallet, to, atoms, account.Nonce + 1, memo);
            await client.SubmitTxAsync(tx, ct);
            StatusText = $"已提交：{LedgerSupply.Format(atoms)} → {to.Hex[..12]}…（tx {tx.Hash[..12]}…，出块约 1 秒）";
            await Task.Delay(600, ct);   // 给出块留半拍，顺带刷余额
            await RefreshBalanceCoreAsync(ct);
        });
    }

    // —— 命令：充值到交易所 ——

    [RelayCommand]
    private async Task DepositAsync(CancellationToken ct = default)
    {
        if (!HasWallet || IsBusy) return;
        var account = DepositAccount.Trim();
        LedgerAddress custody;
        ulong atoms;
        try
        {
            custody = LedgerAddress.Parse(CustodyAddress.Trim());
            atoms = ParseCoins(DepositAmount);
        }
        catch (Exception ex)
        {
            ErrorText = CustodyAddress.Trim().Length == 0
                ? "未配置 custody 地址（设置区）——向运维索取后填写"
                : ex.Message;
            return;
        }
        if (account.Length == 0) { ErrorText = "充值账号必填（memo 归账键——须与期货页登录账号一致）"; return; }

        await RunBusyAsync(async () =>
        {
            var client = CreateNodeClient();
            var wallet = LoadWallet()!;
            await LedgerBridge.DepositAsync(client, wallet, custody, atoms, account, ct);
            StatusText = $"已提交充值：{LedgerSupply.Format(atoms)} → custody（memo={account}）——桥 500ms 轮询 + 对账 1s 内自动补铸现货持仓。";
            await Task.Delay(1500, ct);
            await RefreshBalanceCoreAsync(ct);
        });
    }

    // —— 命令：从交易所提币 ——

    [RelayCommand]
    private async Task WithdrawAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        var user = WithdrawUser.Trim();
        var to = string.IsNullOrWhiteSpace(WithdrawTo) ? Address : WithdrawTo.Trim();
        ulong atoms;
        try
        {
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("交易所账号必填");
            if (string.IsNullOrWhiteSpace(WithdrawPassword)) throw new ArgumentException("密码必填（期货页登录密码）");
            if (to is null) throw new ArgumentException("提币地址必填");
            LedgerAddress.Parse(to);   // 校验
            atoms = ParseCoins(WithdrawAmount);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return;
        }
        if (_exchange is null) { ErrorText = "提币客户端未注册（ILedgerExchangeClient）。"; return; }

        await RunBusyAsync(async () =>
        {
            var txHash = await _exchange.WithdrawAsync(ExchangeAddress.Trim(), user, WithdrawPassword, to, atoms, ct);
            StatusText = $"提币已提交：{LedgerSupply.Format(atoms)} → {to[..12]}…（tx {txHash[..12]}…）——先烧持仓后签名出金，约 1 秒到账。";
            await Task.Delay(1500, ct);
            await RefreshBalanceCoreAsync(ct);
        });
    }

    // —— 内部 ——

    partial void OnNodeAddressChanged(string value) => Persist("wallet.node", value);
    partial void OnCustodyAddressChanged(string value) => Persist("wallet.custody", value);
    partial void OnExchangeAddressChanged(string value) => Persist("wallet.exchange", value);
    partial void OnDepositAccountChanged(string value) => Persist("wallet.account", value);

    private void Persist(string key, string value)
    {
        if (_persisting) return;   // LoadAsync 回填期间不写盘
        Task.Run(async () => { try { await _prefs.SetAsync(key, value); } catch { } });
    }

    private bool _persisting;

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshBalanceCoreAsync(CancellationToken ct)
    {
        if (!HasWallet) return;
        try
        {
            var account = await CreateNodeClient().GetBalanceAsync(Address!, ct);
            BalanceAtoms = account.BalanceAtoms;
            Nonce = account.Nonce;
            BalanceText = LedgerSupply.Format(account.BalanceAtoms);
        }
        catch { /* 刷新尽力而为 */ }
    }

    /// <summary>载入种子文件（存在时）——HasWallet + Address。创建/导入共用。</summary>
    private void LoadExistingWallet()
    {
        try
        {
            var wallet = LoadWallet();
            if (wallet is null) return;
            Address = wallet.Address.Hex;
            HasWallet = true;
            IsCreating = false;
            IsImporting = false;
            NewSeedHex = null;
            ImportSeedText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"钱包种子文件损坏：{ex.Message}（可删除后重新创建/导入——没有备份的种子丢失 = 币丢失）";
        }
    }

    private async Task SaveSeedAsync(string seedHex)
    {
        var dir = Path.GetDirectoryName(_seedFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_seedFilePath, seedHex + Environment.NewLine);
        LoadExistingWallet();
    }

    private LedgerWallet? LoadWallet()
    {
        if (!File.Exists(_seedFilePath)) return null;
        var hex = File.ReadAllText(_seedFilePath).Trim();
        return hex.Length == 64 ? new LedgerWallet(Convert.FromHexString(hex)) : null;
    }

    private LedgerClient CreateNodeClient()
    {
        var addr = NodeAddress.Trim();
        var colon = addr.LastIndexOf(':');
        var (host, port) = colon > 0 && int.TryParse(addr[(colon + 1)..], out var p)
            ? (addr[..colon], p)
            : ("127.0.0.1", 17805);
        return new LedgerClient(host, port);
    }

    /// <summary>币数（字符串，支持 8 位小数）→ 原子数。非法值抛异常（消息带原因）。</summary>
    internal static ulong ParseCoins(string coins)
    {
        if (!decimal.TryParse(coins, out var value) || value <= 0)
            throw new ArgumentException($"币数非法：\"{coins}\"（应为正数，支持 8 位小数，如 1.5）");
        return (ulong)Math.Round(value * (decimal)LedgerSupply.AtomsPerCoin);
    }

    /// <summary>LoadAsync 期间抑制设置回写（prefs 回填触发 OnXChanged → 反向写盘的回环）。</summary>
    internal async Task SuppressPersistDuringAsync(Func<Task> action)
    {
        _persisting = true;
        try { await action(); }
        finally { _persisting = false; }
    }
}
