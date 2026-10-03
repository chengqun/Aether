using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;
using Acme.Securities.Application;
using Acme.Securities.Domain;
using Acme.Desktop.Mvvm;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>行情板单行：主力合约 + 实时行情字段（定时器节流批量刷新，避免 80+ 合约刷爆 UI 线程）。</summary>
public partial class MainFutureQuote : ObservableObject
{
    public string Symbol { get; }
    public string Name { get; }

    [ObservableProperty] private double _lastPrice;
    [ObservableProperty] private double _bidPrice;
    [ObservableProperty] private int _bidVolume;
    [ObservableProperty] private double _askPrice;
    [ObservableProperty] private int _askVolume;
    [ObservableProperty] private double _openInterest;
    [ObservableProperty] private long _volume;
    [ObservableProperty] private double _openPrice;
    [ObservableProperty] private double _preSettlement;
    [ObservableProperty] private string _updateTime = "";

    /// <summary>今日涨幅%（以昨结算 PreSettlement 为基准）。</summary>
    public double ChangePercent => PreSettlement != 0
        ? (LastPrice - PreSettlement) / PreSettlement * 100.0
        : 0;

    /// <summary>买一显示串：有盘口 "价/量"，无盘口显 "--"。</summary>
    public string BidQuote => BidPrice != 0 ? $"{BidPrice:F2}/{BidVolume}" : "--";
    /// <summary>卖一显示串。</summary>
    public string AskQuote => AskPrice != 0 ? $"{AskPrice:F2}/{AskVolume}" : "--";

    partial void OnLastPriceChanged(double value) => OnPropertyChanged(nameof(ChangePercent));
    partial void OnPreSettlementChanged(double value) => OnPropertyChanged(nameof(ChangePercent));
    partial void OnBidPriceChanged(double value) => OnPropertyChanged(nameof(BidQuote));
    partial void OnBidVolumeChanged(int value) => OnPropertyChanged(nameof(BidQuote));
    partial void OnAskPriceChanged(double value) => OnPropertyChanged(nameof(AskQuote));
    partial void OnAskVolumeChanged(int value) => OnPropertyChanged(nameof(AskQuote));

    public MainFutureQuote(string symbol, string name) { Symbol = symbol; Name = name; }
}

/// <summary>
/// CTP 期货 ViewModel（Stockapp 租户）：行情看板 + 交易（资金/持仓/下单/委托/成交/撤单）。
/// 只面向 <see cref="ICtpTradingService"/>（Application 层）+ <see cref="CtpConnectorViewModel"/>（连接）+
/// <see cref="ISecurityManager"/>（主力合约），不直接依赖 Ctp.Net.Core SDK。
/// 镜像 AlphaAgent 的 CtpViewModel，去掉实盘策略（Lean live）部分。
/// </summary>
public partial class CtpViewModel : ObservableObject
{
    private readonly ICtpTradingService _trading;
    private readonly ISecurityManager _securities;
    private readonly ISecurityClientSyncService _securitySync;
    public CtpConnectorViewModel Connector { get; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "未连接";
    [ObservableProperty] private bool _isMdSubscribed;
    [ObservableProperty] private string _loginInfo = "";

    [ObservableProperty] private CtpAccount? _account;
    public ObservableCollection<CtpPosition> Positions { get; } = new();

    /// <summary>主力合约行情板（连接后自动订阅全部，250ms 节流刷新）。</summary>
    public ObservableCollection<MainFutureQuote> QuoteBoard { get; } = new();
    private readonly Dictionary<string, MainFutureQuote> _quoteBySymbol = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>tick 暂存：CTP 线程写入，UI 定时器批量消费。</summary>
    private readonly ConcurrentDictionary<string, CtpTick> _pendingTicks = new();
    private readonly DispatcherTimer _tickTimer;
    private bool _autoSubscribed;

    [ObservableProperty] private MainFutureQuote? _selectedQuote;
    [ObservableProperty] private string _sortColumn = "Symbol";
    [ObservableProperty] private bool _sortDescending;

    public ObservableCollection<CtpOrder> Orders { get; } = new();
    public ObservableCollection<CtpTrade> Trades { get; } = new();

    // 下单输入
    [ObservableProperty] private string _orderInstrumentId = "rb2610";
    [ObservableProperty] private int _orderDirectionIndex;
    [ObservableProperty] private int _orderOffsetIndex;
    [ObservableProperty] private int _orderPriceTypeIndex;
    [ObservableProperty] private double _orderLimitPrice = 3500;
    [ObservableProperty] private int _orderVolume = 1;
    [ObservableProperty] private int _orderTimeConditionIndex;
    [ObservableProperty] private bool _isConfirmingOrder;
    [ObservableProperty] private string _pendingOrderSummary = "";
    [ObservableProperty] private bool _isConfirmingCancel;
    [ObservableProperty] private string _pendingCancelSummary = "";
    private CtpOrder? _pendingCancelOrder;

    public CtpViewModel(ICtpTradingService trading, CtpConnectorViewModel connector, ISecurityManager securities, ISecurityClientSyncService securitySync)
    {
        _trading = trading;
        Connector = connector;
        _securities = securities;
        _securitySync = securitySync;

        _trading.OrderReceived += OnOrderReceived;
        _trading.TradeReceived += OnTradeReceived;
        _trading.ConnectionStateChanged += OnConnectionStateChanged;
        _trading.TickReceived += OnTickReceived;

        _tickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnTickTimerFlush);
        _tickTimer.Start();

        CheckConnectionStatus();
    }

    /// <summary>启动期初始化：刷新连接器配置（含 md 地址/密码，可能触发自动登录）、加载主力合约、尝试自动订阅。
    /// 同时后台从服务端同步证券数据（增量），完成后刷新主力合约看板。</summary>
    public async Task InitializeAsync()
    {
        try { await Connector.RefreshAsync(); }
        catch { /* 连接器刷新失败不阻塞 */ }
        CheckConnectionStatus();
        await LoadMainFuturesAsync();   // 先用本地已有数据展示（上次同步的）
        SyncAndReloadAsync().SafeFireAndForget();       // 后台增量同步服务端证券，完成后刷新看板
    }

    /// <summary>后台从服务端同步证券（增量），完成后在 UI 线程刷新主力合约看板。fire-and-forget。</summary>
    private async Task SyncAndReloadAsync()
    {
        try { await Task.Run(() => _securitySync.SyncFromServerAsync()); }
        catch { /* 同步失败静默，不阻塞期货页 */ }
        await Dispatcher.UIThread.InvokeAsync(async () => await LoadMainFuturesAsync());
    }

    /// <summary>手动同步证券数据（行情 tab 按钮）：从服务端拉取增量 → 刷新主力合约看板。</summary>
    [RelayCommand]
    private async Task SyncSecuritiesAsync()
    {
        StatusMessage = "同步证券数据…";
        try
        {
            var r = await Task.Run(() => _securitySync.SyncFromServerAsync());
            StatusMessage = $"同步完成：{r.SyncedCount} 条";
        }
        catch (Exception ex) { StatusMessage = $"同步失败: {ex.Message}"; }
        await LoadMainFuturesAsync();
    }

    /// <summary>
    /// 加载行情板合约清单（单一漏斗——启动/证券同步重载/手动刷新共用）。<b>行情随柜台</b>：
    /// sim 登录 ⇒ 从 host 上市合约表加载（「我就是交易所」——host 侧 <c>AETHER_CTP_SIM_BOARD</c> 自定义），
    /// 证券同步重载经此分流不会覆盖 sim 板；真实登录 ⇒ 证券库主力合约（原路）。
    /// sim 未登录（连接未建立）⇒ 跳过——Connected 事件后重载（见 <see cref="ApplyConnectionStatus"/>）。
    /// </summary>
    private async Task LoadMainFuturesAsync()
    {
        if (Connector.IsSimFront)
        {
            if (!Connector.IsConnected) return;   // host 上市表要活连接（RPC）；登录后 Connected 事件重载
            try
            {
                var listed = await _trading.QueryInstrumentsAsync();
                if (!Connector.IsSimFront || !Connector.IsConnected) return;   // 加载途中切了柜台——丢弃过期结果
                QuoteBoard.Clear();
                _quoteBySymbol.Clear();
                foreach (var i in listed)
                {
                    // sim id 已是 CTP 规整形态（不经 ToCtpInstrumentCode）；无中文名——名称列显交易所
                    var q = new MainFutureQuote(i.InstrumentId, i.ExchangeId)
                    {
                        // 昨结算来自合约目录（查合约 wire op 已带）——MdTick wire 帧没有 PreSettlement 槽位，
                        // tick.PreSettlement 恒 0 ⇒ ChangePercent 恒 0（涨幅不动——2026-10-03 修复）
                        PreSettlement = i.PreSettlement,
                    };
                    QuoteBoard.Add(q);
                    _quoteBySymbol[i.InstrumentId] = q;
                }
                StatusMessage = listed.Count > 0 ? $"仿真上市合约 {listed.Count} 个" : "仿真柜台无上市合约";
                ApplySort();
                TryAutoSubscribeAsync().SafeFireAndForget();
            }
            catch (Exception ex) { StatusMessage = $"加载上市合约失败: {ex.Message}"; }
            return;
        }

        try
        {
            var mains = await Task.Run(() => _securities.GetMainFuturesAsync());
            if (Connector.IsSimFront) return;   // 加载途中切到 sim——丢弃过期结果（板将由 sim 分支重载）
            QuoteBoard.Clear();
            _quoteBySymbol.Clear();
            foreach (var s in mains)
            {
                // 证券主数据存的 CZCE/中金所代码是小写，CTP 对这两交易所要求大写（区分大小写），不规整订阅会失败。
                var code = ToCtpInstrumentCode(s.Code);
                var q = new MainFutureQuote(code, s.Name);
                QuoteBoard.Add(q);
                _quoteBySymbol[code] = q;
            }
            StatusMessage = mains.Count > 0 ? $"主力合约 {mains.Count} 个" : "无主力合约（请先同步期货数据）";
            ApplySort();
            TryAutoSubscribeAsync().SafeFireAndForget();
        }
        catch (Exception ex) { StatusMessage = $"加载主力合约失败: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshMainFuturesAsync()
    {
        StatusMessage = "加载主力合约…";
        await LoadMainFuturesAsync();
    }

    /// <summary>
    /// 自动订阅全部主力合约。行情前置独立于交易登录——主力列表就绪 + Connector 配置（md 地址/密码）就绪即订阅。幂等。
    /// 一律前置端口（2026-10-01 起）：本地柜台行情 = 前置行情腿 17802（连接器须填 md 前置），
    /// 真实前置同链路——md 配置缺失不订阅（旧「sim 登录无需行情前置」的豁免已随 sim RPC 直连退役删除）。
    /// </summary>
    private async Task TryAutoSubscribeAsync()
    {
        if (_autoSubscribed || QuoteBoard.Count == 0 || IsMdSubscribed) return;
        if (string.IsNullOrEmpty(Connector.MdFrontAddress) || string.IsNullOrEmpty(Connector.Password)) return;
        _autoSubscribed = true;
        try
        {
            StatusMessage = "自动订阅主力合约…";
            var symbols = QuoteBoard.Select(q => q.Symbol).ToList();
            var connect = new CtpMarketDataConnect
            {
                MdFrontAddress = Connector.MdFrontAddress,
                BrokerId = Connector.BrokerId,
                UserId = Connector.UserId,
                Password = Connector.Password,
            };
            await _trading.SubscribeMarketDataAsync(connect, symbols);
            IsMdSubscribed = true;
            StatusMessage = $"已自动订阅 {symbols.Count} 个主力合约";
        }
        catch (Exception ex)
        {
            _autoSubscribed = false;
            StatusMessage = $"自动订阅失败: {ex.Message}";
        }
    }

    /// <summary>断开终态（Disconnected/Closed）重置订阅状态——订阅活在连接上，连接死亡订阅即失效；
    /// 重置后下次 Connected 统一触发「按当前柜台重载板 + 重新订阅」。Reconnecting 刻意不重置：
    /// 真实后端盘中重连由 CtpSessionService 用 _cachedSymbols 自行恢复订阅。</summary>
    private void ResetSubscriptionState()
    {
        _autoSubscribed = false;
        IsMdSubscribed = false;
        _pendingTicks.Clear();
    }

    private void OnConnectionStateChanged(object? sender, CtpConnectionStatus status)
        => Dispatcher.UIThread.Post(() => ApplyConnectionStatus(status));

    private void ApplyConnectionStatus(CtpConnectionStatus status)
    {
        IsConnected = status == CtpConnectionStatus.Connected;
        switch (status)
        {
            case CtpConnectionStatus.Connected:
                StatusMessage = "已连接";
                var r = _trading.LoginResult;
                LoginInfo = $"交易日={r?.TradingDay} FrontID={r?.FrontId} SessionID={r?.SessionId}";
                // 行情随柜台：任何登录成功都按当前柜台重载行情板 + 重新订阅（funnel 内按 IsSimFront 分流：
                // sim⇒host 上市表 / 真实⇒证券库主力合约）。中途切柜台/换账号（连接器驱动，不经本页
                // 「登出」按钮）时旧订阅已随旧连接死亡——断开终态已重置 _autoSubscribed，此处统一
                // 「先换板再订阅」，防止把上一柜台的合约列表订阅到新柜台。盘中 Reconnecting 不重置
                // flags：重连后 CtpSessionService 用 _cachedSymbols 自行恢复订阅，这里尾部早退、不重复订阅。
                LoadMainFuturesAsync().SafeFireAndForget();
                break;
            case CtpConnectionStatus.Reconnecting:
                StatusMessage = "重连中…";
                break;
            case CtpConnectionStatus.Closed:
                ResetSubscriptionState();
                StatusMessage = "闭市，已断开";
                LoginInfo = "";
                break;
            default:  // Disconnected：主动登出 / 切换柜台（连接器先 Logout 再 Login）/ 连接被拒
                ResetSubscriptionState();
                StatusMessage = "未连接";
                LoginInfo = "";
                break;
        }
    }

    private void CheckConnectionStatus()
    {
        IsConnected = _trading.IsConnected;
        if (IsConnected)
        {
            var result = _trading.LoginResult;
            LoginInfo = $"交易日={result?.TradingDay} FrontID={result?.FrontId} SessionID={result?.SessionId}";
            StatusMessage = "已连接";
        }
        else
        {
            StatusMessage = "未连接";
            LoginInfo = "";
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _trading.LogoutAsync().SafeFireAndForget();
        IsConnected = false;
        ResetSubscriptionState();   // 同 Disconnected 事件的重置（事件随后到达再触发一次，幂等）
        Account = null; Positions.Clear(); Orders.Clear(); Trades.Clear();
        IsConfirmingOrder = false; IsConfirmingCancel = false;
        LoginInfo = ""; StatusMessage = "已登出";
    }

    [RelayCommand]
    private async Task QueryAccountAsync()
    {
        if (!_trading.IsConnected) { StatusMessage = "请先登录"; return; }
        try { StatusMessage = "查询资金…"; Account = await _trading.QueryAccountAsync(); StatusMessage = "资金查询完成"; }
        catch (Exception ex) { StatusMessage = $"查询失败: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task QueryPositionAsync()
    {
        if (!_trading.IsConnected) { StatusMessage = "请先登录"; return; }
        try
        {
            StatusMessage = "查询持仓…"; Positions.Clear();
            var list = await _trading.QueryPositionAsync();
            foreach (var p in list) Positions.Add(p);
            StatusMessage = $"持仓 {list.Count} 条";
        }
        catch (Exception ex) { StatusMessage = $"查询失败: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task QueryOrderAsync()
    {
        if (!_trading.IsConnected) { StatusMessage = "请先登录"; return; }
        // 委托 tab 由 OnRtnOrder（F001 推送）实时维护——查询是历史回填（本地前置 SDK 不派发
        // 17B tag 空应答 → 30s 超时，与真前置首查行为一致）。**不清 Orders**——推送攒的活单
        // 是权威数据源，查询失败不能把列表清空（旧版 Orders.Clear() 把 bot 期间的推送记录全毁）。
        try
        {
            StatusMessage = "查询报单…（推送驱动，查询仅历史回填）";
            var list = await _trading.QueryOrderAsync();
            foreach (var o in list)
                if (!Orders.Any(x => x.OrderRef == o.OrderRef && x.FrontId == o.FrontId && x.SessionId == o.SessionId))
                    Orders.Add(o);
            StatusMessage = $"报单 {list.Count} 条（查询）｜列表 {Orders.Count} 条（含推送）";
        }
        catch (Exception ex) { StatusMessage = $"查询超时（已知边界——列表由推送维护: {Orders.Count} 条）"; }
    }

    [RelayCommand]
    private async Task ResubscribeAsync() { _autoSubscribed = false; await TryAutoSubscribeAsync(); }

    [RelayCommand]
    private async Task UnsubscribeAsync()
    {
        _trading.UnsubscribeMarketData();
        IsMdSubscribed = false; _pendingTicks.Clear();
        StatusMessage = "已退订";
    }

    /// <summary>点击表头排序：同列切换升降，换列默认升序。快照排序——live tick 不自动重排。</summary>
    [RelayCommand]
    private void Sort(string column)
    {
        if (string.IsNullOrEmpty(column)) return;
        if (SortColumn == column) SortDescending = !SortDescending;
        else { SortColumn = column; SortDescending = false; }
        ApplySort();
    }

    private void ApplySort()
    {
        if (QuoteBoard.Count == 0) return;
        var col = SortColumn; var desc = SortDescending;
        var items = QuoteBoard.ToList();
        items.Sort((a, b) =>
        {
            bool aActive = a.Volume > 0, bActive = b.Volume > 0;
            if (aActive != bActive) return aActive ? -1 : 1;
            if (!aActive) return string.CompareOrdinal(a.Symbol, b.Symbol);
            int c = col switch
            {
                "Symbol"        => string.CompareOrdinal(a.Symbol, b.Symbol),
                "Name"          => string.CompareOrdinal(a.Name, b.Name),
                "LastPrice"     => a.LastPrice.CompareTo(b.LastPrice),
                "ChangePercent" => a.ChangePercent.CompareTo(b.ChangePercent),
                "BidPrice"      => a.BidPrice.CompareTo(b.BidPrice),
                "AskPrice"      => a.AskPrice.CompareTo(b.AskPrice),
                "OpenInterest"  => a.OpenInterest.CompareTo(b.OpenInterest),
                "Volume"        => a.Volume.CompareTo(b.Volume),
                "UpdateTime"    => string.CompareOrdinal(a.UpdateTime, b.UpdateTime),
                _               => 0,
            };
            return desc ? -c : c;
        });
        for (var i = 0; i < items.Count; i++)
        {
            var cur = QuoteBoard.IndexOf(items[i]);
            if (cur != i) QuoteBoard.Move(cur, i);
        }
    }

    /// <summary>把证券主数据合约代码规整成 CTP 要求格式：CZCE（3 位月份）/中金所（IF/IC/IH/IM/T/TF/TS）→ 大写；上期/大商/能源/广期 → 小写。</summary>
    private static string ToCtpInstrumentCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;
        var m = Regex.Match(code, @"^([A-Za-z]+)(\d+)$");
        if (!m.Success) return code;
        var prefix = m.Groups[1].Value;
        var digits = m.Groups[2].Value;
        var upper = digits.Length == 3 || IsCffexPrefix(prefix);
        prefix = upper ? prefix.ToUpperInvariant() : prefix.ToLowerInvariant();
        return prefix + digits;
    }

    private static bool IsCffexPrefix(string p)
    {
        var u = p.ToUpperInvariant();
        return u is "IF" or "IC" or "IH" or "IM" or "T" or "TF" or "TS";
    }

    private void OnTickReceived(object? sender, CtpTick tick)
    {
        if (!string.IsNullOrEmpty(tick?.InstrumentId))
            _pendingTicks[tick.InstrumentId] = tick;
    }

    private void OnTickTimerFlush(object? sender, EventArgs e)
    {
        if (_pendingTicks.IsEmpty) return;
        foreach (var (symbol, tick) in _pendingTicks)
        {
            if (_quoteBySymbol.TryGetValue(symbol, out var q))
            {
                q.LastPrice = tick.LastPrice;
                q.BidPrice = tick.BidPrice; q.BidVolume = tick.BidVolume;
                q.AskPrice = tick.AskPrice; q.AskVolume = tick.AskVolume;
                q.OpenInterest = tick.OpenInterest;
                q.Volume = tick.Volume;
                q.OpenPrice = tick.OpenPrice;
                // MdTick wire 帧没有 PreSettlement 槽位（SDK 解析恒 0）+ 查合约的 CThostFtdcInstrumentField
                // 也无此字段（C++ wrapper 恒 0）——首帧 tick 的 last 即基价（sim 撮合起点 = BasePrice = PreSettlement，
                // 2026-10-03 涨幅不动修复）。后续 tick 带 PreSettlement > 0 时优先用真值。
                if (tick.PreSettlement > 0) q.PreSettlement = tick.PreSettlement;
                else if (q.PreSettlement == 0 && tick.LastPrice > 0) q.PreSettlement = tick.LastPrice;
                q.UpdateTime = $"{tick.UpdateTime}.{tick.UpdateMillisec:D3}";
            }
        }
        _pendingTicks.Clear();
    }

    private void OnOrderReceived(object? sender, CtpOrder order)
    {
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Orders.Count; i++)
                if (Orders[i].OrderRef == order.OrderRef && Orders[i].FrontId == order.FrontId && Orders[i].SessionId == order.SessionId)
                { Orders[i] = order; return; }
            Orders.Insert(0, order);
        });
    }

    private void OnTradeReceived(object? sender, CtpTrade trade)
        => Dispatcher.UIThread.Post(() => Trades.Insert(0, trade));

    private static CtpDirection DirFromIndex(int i) => i == 0 ? CtpDirection.Buy : CtpDirection.Sell;
    private static CtpOffsetFlag OffsetFromIndex(int i) => i switch { 0 => CtpOffsetFlag.Open, 1 => CtpOffsetFlag.Close, _ => CtpOffsetFlag.CloseToday };
    private static CtpOrderPriceType PriceTypeFromIndex(int i) => i == 0 ? CtpOrderPriceType.Limit : CtpOrderPriceType.Market;
    private static CtpTimeCondition TcFromIndex(int i) => i switch { 0 => CtpTimeCondition.GFD, 1 => CtpTimeCondition.IOC, _ => CtpTimeCondition.FOK };

    [RelayCommand]
    private void RequestOrderInsert()
    {
        if (!_trading.IsConnected) { StatusMessage = "请先登录"; return; }
        var dir = DirFromIndex(OrderDirectionIndex);
        var off = OffsetFromIndex(OrderOffsetIndex);
        var pt = PriceTypeFromIndex(OrderPriceTypeIndex);
        PendingOrderSummary = $"{(dir == CtpDirection.Buy ? "买" : "卖")}{(off == CtpOffsetFlag.Open ? "开" : off == CtpOffsetFlag.Close ? "平" : "平今")} " +
                              $"{OrderInstrumentId} {OrderVolume}手 {(pt == CtpOrderPriceType.Limit ? $"@{OrderLimitPrice}" : "市价")}";
        IsConfirmingOrder = true;
    }

    [RelayCommand]
    private async Task ConfirmOrderInsertAsync()
    {
        IsConfirmingOrder = false;
        if (!_trading.IsConnected) return;
        try
        {
            StatusMessage = "发送下单…";
            var req = new CtpOrderRequest
            {
                BrokerId = _trading.BrokerId ?? "",
                InvestorId = _trading.UserId ?? "",
                InstrumentId = OrderInstrumentId,
                Direction = DirFromIndex(OrderDirectionIndex),
                OffsetFlag = OffsetFromIndex(OrderOffsetIndex),
                PriceType = PriceTypeFromIndex(OrderPriceTypeIndex),
                LimitPrice = OrderLimitPrice,
                Volume = OrderVolume,
                TimeCondition = TcFromIndex(OrderTimeConditionIndex),
            };
            var r = await _trading.PlaceOrderAsync(req);
            StatusMessage = $"已提交 OrderRef={r.OrderRef}";
        }
        catch (Exception ex) { StatusMessage = $"下单失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void CancelOrderConfirm() => IsConfirmingOrder = false;

    [RelayCommand]
    private void RequestOrderAction(CtpOrder? order)
    {
        if (order is null) return;
        _pendingCancelOrder = order;
        PendingCancelSummary = $"撤单 {order.InstrumentId} ref={order.OrderRef} {order.Direction}{order.OffsetFlag} {order.VolumeTotalOriginal}@{order.LimitPrice}";
        IsConfirmingCancel = true;
    }

    [RelayCommand]
    private async Task ConfirmOrderActionAsync()
    {
        IsConfirmingCancel = false;
        var order = _pendingCancelOrder; _pendingCancelOrder = null;
        if (order is null || !_trading.IsConnected) return;
        try
        {
            StatusMessage = "发送撤单…";
            var orderRef = new CtpOrderRef(
                _trading.BrokerId ?? "", _trading.UserId ?? "",
                order.FrontId, order.SessionId, order.OrderRef, order.ExchangeId);
            await _trading.CancelOrderAsync(orderRef);
            StatusMessage = $"已撤单 ref={order.OrderRef}";
        }
        catch (Exception ex) { StatusMessage = $"撤单失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void CancelOrderActionConfirm() => IsConfirmingCancel = false;
}
