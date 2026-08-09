using Acme.Desktop.Mvvm;
using Acme.StockTrading.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>下单方式（股票经纪后端）。</summary>
public enum OrderMethod { FlaUI, Mcp }

/// <summary>
/// 路由型 <see cref="IStockBrokerage"/>：同时持有 FlaUI 后端与 MCP（AppAutomation）后端，
/// 按 <see cref="Method"/> 实时委派给当前激活的那个——运行时切换，无需重启。
/// <para>两个后端经 keyed services 注入（"flaui" / "mcp"）。本类注册为默认 <see cref="IStockBrokerage"/>，
/// 被 <c>IStockTradingApplicationService</c> 与连接器/股票页消费。<see cref="Method"/> 持久化到
/// <see cref="IPreferencesStore"/>（key <c>stocktrade.method</c>）。</para>
/// <para>Mac 上两后端皆惰性（FlaUI 桩 + MCP 未连 server）；Windows 上换真 <c>IMcpToolInvoker</c> 后 MCP 即活。</para>
/// </summary>
public sealed partial class RoutingStockBrokerage : ObservableObject, IStockBrokerage
{
    private readonly IStockBrokerage _flaUi;
    private readonly IStockBrokerage _mcp;
    private readonly IPreferencesStore? _prefs;

    public RoutingStockBrokerage(
        [FromKeyedServices("flaui")] IStockBrokerage flaUi,
        [FromKeyedServices("mcp")] IStockBrokerage mcp,
        IPreferencesStore? preferences = null)
    {
        _flaUi = flaUi ?? throw new ArgumentNullException(nameof(flaUi));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _prefs = preferences;

        // 启动期读持久化的下单方式（JsonPreferencesStore.GetAsync 同步完成，可 GetAwaiter）
        var saved = preferences?.GetAsync("stocktrade.method").GetAwaiter().GetResult();
        _method = saved == "mcp" ? OrderMethod.Mcp : OrderMethod.FlaUI;

        _flaUi.LogReceived += (_, s) => LogReceived?.Invoke(this, s);
        _mcp.LogReceived += (_, s) => LogReceived?.Invoke(this, s);
    }

    /// <summary>当前下单方式。改后持久化；连接器/股票页据此刷新状态。</summary>
    [ObservableProperty] private OrderMethod _method;

    partial void OnMethodChanged(OrderMethod value)
        => _ = _prefs?.SetAsync("stocktrade.method", value == OrderMethod.Mcp ? "mcp" : "flaui");

    private IStockBrokerage Active => Method == OrderMethod.Mcp ? _mcp : _flaUi;

    /// <summary>
    /// 任一后端已连接即视为已连接。连接器卡片绑此属性——它反映「同花顺窗口物理可达」这一事实，
    /// 不应随下单方式（FlaUI/MCP）切换而变（用户切到 MCP 时，FlaUI 后端可能仍连着；切回去也不该瞬间「掉线」）。
    /// 下单操作仍走 <see cref="Active"/>（当前选中的后端）；若该后端未连，操作会自行报错。
    /// </summary>
    public bool IsConnected => _flaUi.IsConnected || _mcp.IsConnected;

    /// <summary>指定后端是否已连接（供股票页切换时确认目标后端就绪，而非看整体 <see cref="IsConnected"/>）。</summary>
    public bool IsConnectedByMethod(OrderMethod method) => method == OrderMethod.Mcp ? _mcp.IsConnected : _flaUi.IsConnected;

    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default) => Active.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => Active.DisconnectAsync(ct);
    public Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default) => Active.LaunchClientAsync(ct);
    public Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default) => Active.PlaceOrderAsync(request, ct);
    public Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default) => Active.CancelOrderAsync(orderNo, ct);
    public Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default) => Active.QueryPositionsAsync(ct);
}
