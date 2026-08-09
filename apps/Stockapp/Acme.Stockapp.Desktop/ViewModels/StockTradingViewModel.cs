using System.Collections.ObjectModel;
using Acme.Desktop.Mvvm;
using Acme.StockTrading.Application;
using Acme.StockTrading.Domain;
using Acme.Stockapp.Desktop.Hosting;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// 股票交易页 ViewModel：经 <see cref="IStockTradingApplicationService"/> 操控同花顺下单客户端。
/// 两种下单方式（FlaUI 本地 / MCP 固定过程）经 <see cref="RoutingStockBrokerage"/> 运行时切换，
/// 页面顶部的「下单方式」选择器绑定 <see cref="UseMcp"/>。UI/AI 都走 Application 契约，不直接依赖 Infrastructure。
/// 区别于 TradingViewModel（Lean 期货回测/实盘）——这是 A 股 GUI 自动化下单。
/// </summary>
public partial class StockTradingViewModel : ObservableObject
{
    private readonly IStockTradingApplicationService _service;
    private readonly RoutingStockBrokerage _router;
    private readonly Dispatcher _dispatcher;

    public StockTradingViewModel(IStockTradingApplicationService service, RoutingStockBrokerage router)
    {
        _service = service;
        _router = router;
        _dispatcher = Dispatcher.UIThread;
        _service.LogReceived += OnLogReceived;
        _useMcp = router.Method == OrderMethod.Mcp;   // 启动期读持久化的下单方式
        RefreshState();   // 启动期读一次连接态（连接由连接器 tab 负责）
        // 启动期若当前后端未连接，自动确认一次（确认同花顺窗口在不在）——免得用户第一次开股票页
        // 看到「未连接」、还得手动切到连接器 tab 触发重连。连接器 tab 的 RefreshAsync 也会在切过去时兜底。
        if (!_router.IsConnectedByMethod(router.Method))
            _ = ConnectCurrentAsync();
    }

    // —— 下单方式（FlaUI / MCP 运行时切换）——
    [ObservableProperty] private bool _useMcp;
    partial void OnUseMcpChanged(bool value)
    {
        _router.Method = value ? OrderMethod.Mcp : OrderMethod.FlaUI;
        RefreshState();   // 切后端 → 连接态随之变
        // 切到目标后端且该后端尚未连接时，自动发起一次连接确认（确认同花顺窗口在不在），
        // 免得用户切了 MCP 还得跑去连接器 tab 点连接。看的是目标后端自身状态（而非 router 整体，
        // 因为 IsConnected 是「任一连接」，FlaUI 连着时不能据此跳过 MCP 的连接确认）。
        var target = value ? OrderMethod.Mcp : OrderMethod.FlaUI;
        if (!_router.IsConnectedByMethod(target))
            _ = ConnectCurrentAsync();
    }

    /// <summary>对当前选中的后端发起一次连接确认（异步、不阻塞 UI；结果经日志事件刷新状态）。</summary>
    private async Task ConnectCurrentAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, err) = await _service.ConnectAsync();
            // ConnectAsync 内部会发 LogReceived → OnLogReceived → RefreshState，但保险起见再刷一次。
            if (!ok && !string.IsNullOrEmpty(err))
                _dispatcher.Post(() => StatusMessage = $"未连接：{err}");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    // —— 连接态 ——
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "未连接";

    // —— 下单表单 ——
    [ObservableProperty] private string _symbol = "600000";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _directionIndex; // 0=买 1=卖
    [ObservableProperty] private decimal _limitPrice = 10.00m;
    [ObservableProperty] private int _quantity = 100;
    [ObservableProperty] private bool _isBusy;

    // —— 撤单 ——
    [ObservableProperty] private string _cancelOrderNo = "";

    // —— 持仓 ——
    [ObservableProperty]
    private ObservableCollection<StockPosition> _positions = new();

    // —— 日志 ——
    [ObservableProperty]
    private ObservableCollection<string> _logs = new();

    public StockDirection Direction => DirectionIndex == 0 ? StockDirection.Buy : StockDirection.Sell;

    private void OnLogReceived(object? sender, string msg)
    {
        // FlaUI/STA 后台线程触发的事件，切回 UI 线程刷新集合 + 连接态。
        _dispatcher.Post(() =>
        {
            const int max = 500;
            Logs.Add(msg);
            while (Logs.Count > max) Logs.RemoveAt(0);
            RefreshState();   // 连接/操作有日志 → 顺带刷新状态（连接在连接器 tab 触发）
        });
    }

    private void RefreshState()
    {
        IsConnected = _service.IsConnected;
        StatusMessage = IsConnected ? "已连接" : "未连接";
    }

    [RelayCommand(CanExecute = nameof(CanPlace))]
    private async Task PlaceOrderAsync()
    {
        if (string.IsNullOrWhiteSpace(Symbol)) return;
        IsBusy = true;
        try
        {
            var req = new StockOrderRequest
            {
                Symbol = Symbol.Trim(),
                Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
                Direction = Direction,
                LimitPrice = LimitPrice,
                Quantity = Quantity,
            };
            var (ok, err) = await _service.PlaceOrderAsync(req);
            StatusMessage = ok ? $"已下单 {req.Symbol} {req.Direction} {req.Quantity}@{req.LimitPrice}" : $"下单失败: {err}";
        }
        finally { IsBusy = false; }
    }

    private bool CanPlace => IsConnected && !IsBusy;

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (string.IsNullOrWhiteSpace(CancelOrderNo)) return;
        IsBusy = true;
        try
        {
            var (ok, err) = await _service.CancelOrderAsync(CancelOrderNo.Trim());
            StatusMessage = ok ? $"已撤单 {CancelOrderNo}" : $"撤单失败: {err}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task QueryPositionsAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _service.QueryPositionsAsync();
            Positions.Clear();
            foreach (var p in list) Positions.Add(p);
            StatusMessage = $"已刷新持仓 ({list.Count} 条)";
        }
        finally { IsBusy = false; }
    }

    partial void OnIsConnectedChanged(bool value) => PlaceOrderCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => PlaceOrderCommand.NotifyCanExecuteChanged();
}
