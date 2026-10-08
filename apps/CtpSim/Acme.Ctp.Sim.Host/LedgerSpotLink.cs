using Aether.Ledger;
using Acme.Ctp.Simulator;

// 全局命名空间（host 惯例——同 Program.cs 的 Log/ExchangeHostedService；tests 引 exe 程序集可直接用）

/// <summary>
/// 现货桥接线（批 4 收尾——「充币 → 铸造持仓」的最后一焊）：把链侧 IOU 与交易所核心的现货持仓对齐。
/// 拓扑：LedgerBridge（链侦听）+ SimTradingCore（venue 账本）之间 <b>只有这一个类</b>，住在交易所宿主
/// （composition root——两侧领域零互引：Aether.Ledger 纯库不碰 venue，Simulator 纯库不碰链）。
///
/// <para><b>两个真相 + 一个对账循环</b>（任何崩溃窗口都收敛，无需额外持久化）：</para>
/// <list type="bullet">
/// <item>链侧真相：per-account IOU（桥，从链全量重扫推导——充币 + / 提币 −，clamp 0）= 该账户的<b>净铸造目标</b>（手数）。</item>
/// <item>DB 侧真相：sim_spot_minted 铸造台账（Credit/Burn 自动维护、随持仓同库落盘）= <b>已铸造量</b>。</item>
/// <item>每秒对账：差正 → <see cref="SimTradingCore.CreditSpotPosition"/> 补铸；差负 → BurnSpotPosition 烧毁超额。
/// 交易（买开/卖平）只转移持仓<b>不改变两侧</b>——对账天然不感知交易。</item>
/// </list>
///
/// <para><b>提币闸门 = venue 现货持仓</b>（经 <see cref="ILedgerWithdrawHook"/>，签名前烧毁）——不是 IOU：
/// 「买入后提币」的账户 IOU=0 但持仓>0（真实交易所语义）；烧毁带上已付价款（margin 消耗）。</para>
///
/// <para><b>供给约束</b>：撮合不增 OI（现货禁开空的结构性保证）+ Credit/Burn 是唯一铸造入口 ⇒
/// 总持仓 ≤ custody 链上余额恒成立；<see cref="SimTradingCore.SetSupplyLimit"/> 只是把 custody 余额同步成
/// 信息性字段（可查不卡撮合）。</para>
///
/// <para><b>粉尘语义</b>：atoms → 手数向下取整（ax：1 手 = 1 币 = 1e8 atoms）——不足 1 手的零头不铸造、
/// 目标值稳定（不会反复触发对账）；整手提币才允许烧毁（粉尘留在链上 custody 里）。</para>
///
/// <para><b>未上币待命</b>：BOARD 没上架现货合约 ⇒ 对账跳过（log 一次状态切换）——
/// <c>AETHER_CTP_SIM_BOARD</c> 加上现货 id 即自动激活，无需重启交易所。</para>
/// </summary>
public sealed class LedgerSpotLink : ILedgerWithdrawHook, IDisposable
{
    private readonly SimTradingCore _core;
    private readonly LedgerBridge _bridge;
    private readonly LedgerClient _client;
    private readonly string _instrumentId;
    private readonly TimeSpan _interval;
    private Timer? _timer;
    private int _reconciling;          // 对账重入守卫（timer tick 与 ReconcileNow 互斥）
    private bool _spotListed;          // 上币状态（日志去重——只在切换时打）
    private readonly Dictionary<string, string> _lastBurnErrors = new(StringComparer.Ordinal);   // 烧毁失败日志去重

    public LedgerSpotLink(SimTradingCore core, LedgerBridge bridge, LedgerClient client,
        string instrumentId = "ax001", TimeSpan? reconcileInterval = null)
    {
        _core = core;
        _bridge = bridge;
        _client = client;
        _instrumentId = instrumentId;
        _interval = reconcileInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>启动：首轮对账 + 周期对账（周期默认 1s——链出块间隔秒级，足够灵敏）。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        Reconcile("启动对账");
        _timer = new Timer(_ => Reconcile("对账"), null, _interval, _interval);
        return Task.CompletedTask;
    }

    /// <summary>提币钩子：烧毁 venue 侧现货持仓（桥签名出金之前）。整手校验 + 持仓闸门。</summary>
    public Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(string accountKey, ulong amountAtoms, CancellationToken ct = default)
    {
        var instrument = _core.TryGetInstrument(_instrumentId);
        if (instrument is not { IsSpot: true })
            return Task.FromResult((false, $"{_instrumentId} 未上市或非现货——venue 无法处理提币"));
        var atomsPerLot = AtomsPerLot(instrument);
        if (amountAtoms % atomsPerLot != 0)
            return Task.FromResult((false, $"提币量须为整手（{atomsPerLot} atoms/手，粉尘留在链上 custody）"));
        try
        {
            _core.BurnSpotPosition(_instrumentId, accountKey, checked((int)(amountAtoms / atomsPerLot)));
            return Task.FromResult((true, (string?)null));
        }
        catch (OverflowException)
        {
            return Task.FromResult((false, "提币量超出 int 手数范围"));
        }
        catch (Acme.Ctp.Domain.CtpBusinessException ex)
        {
            return Task.FromResult((false, ex.ErrorMsg));
        }
    }

    /// <summary>同步对账一轮（tests 用——确定性驱动，不等 timer）。</summary>
    public void ReconcileNow() => Reconcile("手动对账");

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // —— 内部 ——

    private void Reconcile(string tag)
    {
        if (Interlocked.CompareExchange(ref _reconciling, 1, 0) != 0) return;   // 重入守卫
        try
        {
            var instrument = _core.TryGetInstrument(_instrumentId);
            if (instrument is not { IsSpot: true })
            {
                if (_spotListed)
                    Log.Line($"[LEDGER-SPOT] {_instrumentId} 未上市/非现货——现货桥待命（AETHER_CTP_SIM_BOARD 上架后自动激活）");
                _spotListed = false;
                return;
            }
            if (!_spotListed)
            {
                Log.Line($"[LEDGER-SPOT] 现货桥就绪：{_instrumentId}（对账周期 {_interval.TotalMilliseconds:0}ms）");
                _spotListed = true;
            }

            var atomsPerLot = AtomsPerLot(instrument);

            // 铸造台账（DB 侧真相）对齐链侧 IOU（目标）——交易两侧都不感知，只动差额
            var iou = _bridge.GetAllCoinBalances();
            var minted = _core.GetSpotMintedVolumes(_instrumentId);
            foreach (var account in iou.Keys.Concat(minted.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                iou.TryGetValue(account, out var atoms);
                var targetLots = checked((int)(atoms / atomsPerLot));   // ≤ 21M 手（链供给上限）——int 安全
                var currentLots = minted.TryGetValue(account, out var lots) ? lots : 0;
                var delta = targetLots - currentLots;
                if (delta > 0)
                {
                    _core.CreditSpotPosition(_instrumentId, account, delta);
                    Log.Line($"[LEDGER-SPOT] 补铸 {delta} 手 → {account}（IOU {LedgerSupply.Format(atoms)}，台账已同步）");
                }
                else if (delta < 0)
                {
                    try
                    {
                        _core.BurnSpotPosition(_instrumentId, account, -delta);
                        Log.Line($"[LEDGER-SPOT] 烧毁 {-delta} 手 ← {account}（对账回收超额铸造）");
                    }
                    catch (Acme.Ctp.Domain.CtpBusinessException ex) when (ex.ErrorId == SimErrorCodes.CloseVolumeExceeds)
                    {
                        // 持仓不足烧不动（如已卖出的存量）——同消息只记一次、后续轮次静默重试（修复靠新链事件）
                        if (!_lastBurnErrors.TryGetValue(account, out var last) || last != ex.ErrorMsg)
                        {
                            _lastBurnErrors[account] = ex.ErrorMsg;
                            Log.Line($"[LEDGER-SPOT] ⚠ 对账烧毁受阻（{account}）：{ex.ErrorMsg}");
                        }
                    }
                }
            }

            // 供给上限（信息性——撮合不卡，结构性闸门在 Credit/Burn + 禁开空）：custody 链上余额 → 手数
            try
            {
                var custody = _client.GetBalanceAsync(_bridge.CustodyAddress.Hex).GetAwaiter().GetResult();
                _core.SetSupplyLimit(_instrumentId, checked((int)(custody.BalanceAtoms / atomsPerLot)));
            }
            catch (LedgerClientException)
            {
                // 链节点暂不可达——供给上限留旧值（信息性字段，下轮重试）
            }
        }
        catch (Exception ex)
        {
            Log.Line($"[LEDGER-SPOT] {tag}异常（下一轮重试）：{ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _reconciling, 0);
        }
    }

    /// <summary>1 手的原子数 = 每币原子数 × 合约乘数（ax：1e8 × 1）。调用前提：instrument 已知。</summary>
    private static ulong AtomsPerLot(SimInstrument instrument)
        => Math.Max(1UL, (ulong)Math.Round(LedgerSupply.AtomsPerCoin * instrument.VolumeMultiple));
}
