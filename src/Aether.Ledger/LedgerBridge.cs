using System.Text.Json;

namespace Aether.Ledger;

/// <summary>
/// 提币前置钩子（venue 接线——交易所宿主的 <c>LedgerSpotLink</c> 实现）：桥签名出金<b>之前</b>先烧毁
/// venue 侧的可提取资产（现货 Long 持仓）。返回 <c>(false, 原因)</c> = venue 侧余额不足/未上币 ⇒ 拒提
/// （IOU 不动、不签名）。烧毁成功但链上提交失败的崩溃窗口由「链是唯一真相」重扫 + venue 对账自愈
/// （烧掉的持仓会在下一轮对账补铸回来——币没走，仓位也不能丢）。
/// </summary>
public interface ILedgerWithdrawHook
{
    /// <summary>asset：<see cref="LedgerSupply.Assets.Coin"/>（烧现货持仓）或 <see cref="LedgerSupply.Assets.Quote"/>（扣场内现金）。</summary>
    Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(string accountKey, ulong amountAtoms, uint asset, CancellationToken ct = default);
}

/// <summary>
/// 交易所桥（批 3）：链侧**托管服务**——交易所进程持有的一对密钥（custody wallet）+ 充币侦听 + 提币签名。
/// <para>
/// **IOU 状态完全从链可推导**（无独立持久化——重启 = 全量重扫链重建）：
/// <list type="bullet">
/// <item>充币：链上 tx <c>To == custody 地址</c> + <c>memo = 内部账户键</c> ⇒ 该账户 IOU +amount</item>
/// <item>提币：链上 tx <c>From == custody 地址</c> + <c>memo = 内部账户键</c> ⇒ 该账户 IOU −amount</item>
/// </list>
/// 提币时立即更新内存态（链上确认由下一轮 poll 自洽——临时超前无害，重启后链是唯一真相）。
/// </para>
/// <para>
/// 归账靠 <b>memo</b>：agent 充值时签名 tx 带 memo = 自己的内部账户键（交易所侧验链上交易 + memo）。
/// 无 memo 的充值跳过（无法归账——诚实边界，不静默丢失：链上余额仍在，但 IOU 不记账）。
/// </para>
/// <para>
/// **签名在桥**（提币从 custody 出金须 custody 私钥——桥持有它）；充币的签名在 agent 钱包侧（桥只侦听）。
/// <b>诚实边界</b>：桥的密钥即交易所的托管责任——泄漏 = 托管币可被转走（FTX 的教训）。
/// </para>
/// </summary>
public sealed class LedgerBridge : IDisposable
{
    private readonly LedgerClient _client;
    private readonly LedgerWallet _custody;
    private readonly Dictionary<string, ulong> _balances = new(StringComparer.Ordinal);          // 币 IOU（To==custody, asset=Coin）
    private readonly Dictionary<string, ulong> _quoteBalances = new(StringComparer.Ordinal);     // 计价币 IOU（asset=Quote——批 6 双资产）
    private ulong _custodyNonce;   // custody 的下一个出金 nonce（单流覆盖两资产；重启从链重扫恢复）
    private ulong _scannedHeight;  // 已处理到的最大块高
    private Timer? _pollTimer;
    private readonly object _gate = new();
    private readonly Action<string> _log;

    /// <summary>托管地址（custody 钱包的链上地址）。</summary>
    public LedgerAddress CustodyAddress => _custody.Address;

    /// <summary>桥初始化是否完成（StartAsync 后为 true——充币侦听与提币须在初始化后调用）。</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// 提币前置钩子（venue 接线）：设了它，提币闸门 = venue 侧现货持仓（真实交易所语义——「买了币的账户
    /// IOU=0 但持仓>0，照提」），烧毁成功才借记 + 签名；未设 = 批 3 语义（纯 IOU 闸门）。StartAsync 前设置。
    /// </summary>
    public ILedgerWithdrawHook? WithdrawHook { get; set; }

    public LedgerBridge(LedgerClient client, LedgerWallet custodyWallet, Action<string>? log = null)
    {
        _client = client;
        _custody = custodyWallet;
        _log = log ?? (msg => Console.WriteLine($"[LEDGER-BRIDGE] {msg}"));
    }

    /// <summary>
    /// 启动：全量重扫链（重建 IOU 状态 + custody nonce）+ 启动轮询。
    /// 链是唯一真相——桥的内存态重启后从这里恢复。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var info = await _client.GetInfoAsync(ct);
        _log($"桥启动：链创世 {info.GenesisHash[..12]}… | 当前高度 {info.Height} | custody {CustodyAddress.Hex[..12]}…");

        // 全量重扫：逐块处理，重建 IOU 余额 + custody nonce
        for (ulong h = 1; h <= info.Height; h++)
        {
            var block = await _client.GetBlockAsync(h, ct);
            ProcessBlock(block);
        }
        _scannedHeight = info.Height;
        _log($"链重扫完成：高度 {info.Height} | 币 IOU {_balances.Count} 个 | 计价币 IOU {_quoteBalances.Count} 个 | custody 出金 nonce={_custodyNonce}");

        // 轮询（500ms——链出块间隔最小也是秒级，500ms 足够灵敏）
        _pollTimer = new Timer(async _ => await PollAsync(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        IsReady = true;
    }

    /// <summary>查账户币 IOU 余额（原子单位；未知账户 = 0）。</summary>
    public Task<ulong> GetCoinBalanceAsync(string accountKey, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_balances.TryGetValue(accountKey, out var balance) ? balance : 0UL);
    }

    /// <summary>查账户计价币 IOU 余额（原子单位；未知账户 = 0）——场内现金的铸造目标。</summary>
    public Task<ulong> GetQuoteBalanceAsync(string accountKey, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_quoteBalances.TryGetValue(accountKey, out var balance) ? balance : 0UL);
    }

    /// <summary>列出全部币 IOU 余额（快照）。</summary>
    public IReadOnlyDictionary<string, ulong> GetAllCoinBalances()
    {
        lock (_gate) return new Dictionary<string, ulong>(_balances, StringComparer.Ordinal);
    }

    /// <summary>列出全部计价币 IOU 余额（快照）——对账用。</summary>
    public IReadOnlyDictionary<string, ulong> GetAllQuoteBalances()
    {
        lock (_gate) return new Dictionary<string, ulong>(_quoteBalances, StringComparer.Ordinal);
    }

    /// <summary>
    /// 提币：检查余额 → 借记 → custody 签名链上转账 → 提交节点。返回链上交易哈希。
    /// asset：币（烧现货持仓）/ 计价币（扣场内现金——批 6）。memo = accountKey（链上可推导——重启重扫自洽）。
    /// <para>闸门二态（<see cref="WithdrawHook"/>）：设钩子 = venue 侧闸门（币=现货持仓、计价币=可用现金；
    /// 「买入后提币」IOU 可为 0 照提；借记 clamp 到 0 不为负，与重扫 debit 同语义）；未设 = 纯 IOU 闸门（批 3 语义）。</para>
    /// </summary>
    public async Task<string> WithdrawAsync(string accountKey, LedgerAddress to, ulong amountAtoms,
        uint asset = LedgerSupply.Assets.Coin, CancellationToken ct = default)
    {
        if (!IsReady) throw new InvalidOperationException("桥未初始化（须先 StartAsync）。");
        if (amountAtoms == 0) throw new ArgumentException("提币量必须 > 0。");

        // venue 钩子先烧毁（签名之前——烧不掉就不签：venue 不欠这个账户那么多可提取物）
        if (WithdrawHook is { } hook)
        {
            var (ok, error) = await hook.TryBurnForWithdrawalAsync(accountKey, amountAtoms, asset, ct);
            if (!ok)
                throw new LedgerClientException(
                    $"venue 拒绝提币：{error ?? "持仓/现金不足"}（账户 {accountKey}，资产 {asset}，提币 {LedgerSupply.Format(amountAtoms)}）");
        }

        var iou = asset == LedgerSupply.Assets.Coin ? _balances : _quoteBalances;
        ulong currentBalance;
        lock (_gate)
        {
            iou.TryGetValue(accountKey, out currentBalance);
            if (WithdrawHook is null && currentBalance < amountAtoms)
                throw new LedgerClientException($"IOU 余额不足：账户 {accountKey} 余额 {currentBalance} < 提币 {amountAtoms}");
            // 借记（clamp 到 0——钩子模式下 IOU 已为 0 的「买入后提币」不 underflow；
            // 链上确认由下一轮 poll 自洽——临时超前无害，重扫 debit 同款 clamp）
            iou[accountKey] = currentBalance >= amountAtoms ? currentBalance - amountAtoms : 0UL;
            _custodyNonce++;
        }

        // custody 签名出金（memo = accountKey——链上可推导归账）
        var tx = LedgerTransaction.Sign(
            await GetChainIdAsync(ct), _custody, to, amountAtoms, _custodyNonce, accountKey, asset);
        await _client.SubmitTxAsync(tx, ct);
        var assetName = asset == LedgerSupply.Assets.Coin ? "币" : "计价币";
        _log($"提币（{assetName}）：账户 {accountKey} → {to.Hex[..12]}… {LedgerSupply.Format(amountAtoms)}（tx {tx.Hash[..12]}… nonce={_custodyNonce}）");
        return tx.Hash;
    }

    /// <summary>充币的便捷入口（agent 钱包签名 → 提交节点；桥只负责侦听——本方法不归账，归账由 poll 完成）。
    /// asset：币（铸造现货持仓）/ 计价币（变成场内现金）。调用方（agent/测试）用 wallet.Sign 转账到
    /// custody 地址 + memo = 自己的账户键。</summary>
    public static async Task DepositAsync(LedgerClient client, LedgerWallet agentWallet, LedgerAddress custodyAddress,
        ulong amountAtoms, string accountKey, uint asset = LedgerSupply.Assets.Coin, CancellationToken ct = default)
    {
        var info = await client.GetInfoAsync(ct);
        // agent 侧 nonce 须从节点查（agent 可能不在本进程）
        var account = await client.GetBalanceAsync(agentWallet.Address.Hex, ct);
        var nonce = account.Nonce + 1;
        var tx = LedgerTransaction.Sign(info.ChainId, agentWallet, custodyAddress, amountAtoms, nonce, accountKey, asset);
        await client.SubmitTxAsync(tx, ct);
    }

    // —— 内部：轮询与块处理 ——

    private async Task PollAsync()
    {
        try
        {
            var info = await _client.GetInfoAsync();
            if (info.Height > _scannedHeight)
            {
                for (ulong h = _scannedHeight + 1; h <= info.Height; h++)
                {
                    var block = await _client.GetBlockAsync(h);
                    ProcessBlock(block);
                }
                _scannedHeight = info.Height;
            }
        }
        catch (Exception ex)
        {
            _log($"轮询异常（链节点不可达？下次重试）：{ex.Message}");
        }
    }

    /// <summary>处理一个块：充币（To==custody + memo ⇒ 按资产 credit 对应 IOU）与提币确认
    ///（From==custody + memo ⇒ 幂等跳过——提币时已即时借记，这里只推进 nonce（重扫路径才真正 debit））。</summary>
    private void ProcessBlock(LedgerBlock block)
    {
        foreach (var tx in block.Transactions)
        {
            if (tx.To == CustodyAddress.Hex)
            {
                // 充币：memo = 归账键；资产路由（币 IOU / 计价币 IOU）
                var accountKey = tx.Memo;
                if (string.IsNullOrWhiteSpace(accountKey))
                {
                    var assetName = tx.Asset == LedgerSupply.Assets.Coin ? "" : "（计价币）";
                    _log($"⚠ 无 memo 充币{assetName}（无法归账——链上余额在，IOU 不记）：from={tx.From[..12]}… {LedgerSupply.Format(tx.AmountAtoms)}");
                    continue;
                }
                var iou = tx.Asset == LedgerSupply.Assets.Coin ? _balances : _quoteBalances;
                lock (_gate)
                {
                    iou.TryGetValue(accountKey, out var balance);
                    iou[accountKey] = balance + tx.AmountAtoms;
                }
                var name = tx.Asset == LedgerSupply.Assets.Coin ? "币" : "计价币";
                _log($"充币（{name}）：账户 {accountKey} +{LedgerSupply.Format(tx.AmountAtoms)}（from={tx.From[..12]}…）");
            }
            else if (tx.From == CustodyAddress.Hex)
            {
                // 提币（我们签的出金）：正常路径已即时借记——重扫路径才真正 debit；按资产路由
                if (tx.Memo is { Length: > 0 } memo)
                {
                    var iou = tx.Asset == LedgerSupply.Assets.Coin ? _balances : _quoteBalances;
                    lock (_gate)
                    {
                        // 只有重扫路径（IsReady=false 时首次扫描）才需要真正 debit；
                        // 正常运行路径已即时借记，这里幂等跳过（余额不会变负——即时借记已保证）
                        if (!IsReady)
                        {
                            iou.TryGetValue(memo, out var balance);
                            iou[memo] = balance >= tx.AmountAtoms ? balance - tx.AmountAtoms : 0;
                        }
                    }
                }
                // custody 出金 nonce 推进（单流覆盖两资产——无论哪条路径，nonce 始终 = 链上出金计数）
                lock (_gate) _custodyNonce = Math.Max(_custodyNonce, tx.Nonce);
            }
        }
    }

    private async Task<uint> GetChainIdAsync(CancellationToken ct)
    {
        var info = await _client.GetInfoAsync(ct);
        return info.ChainId;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
}
