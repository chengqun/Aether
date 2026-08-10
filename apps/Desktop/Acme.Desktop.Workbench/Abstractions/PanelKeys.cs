namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// Panel key 常量目录——view-key（<see cref="IViewRegistry"/> 解析视图）+ region id（<see cref="LayoutRegion"/>/<see cref="LayoutTabbed"/> 引用槽位）
/// 的单一真相源。workspace 定义、view registry、租户 contributor 三处必须用同一 key 串，散落字面量会因拼写不一致导致 panel 静默不渲染
/// （<c>CreateView</c> 返回 null → ContentControl 空）。全部经此引用，编译期防拼写错误、重命名一处生效。
/// </summary>
/// <remarks>
/// workspace 定义里 <see cref="PanelDescriptor"/> 的 RegionId 与 Key 通常相同（panel 既是某 region 的初始内容、又按 key 解析视图）；
/// 但概念上二者不同——注入插件的 panel 的 Key 决定渲染什么、RegionId 无意义（它被 <see cref="IRegionManager.AddToRegion"/> 的 name 参数定位宿主）。
/// 故 catalog 同时含 panel key（Chat/Notes/…）与专用 region 名（Inject）。
/// </remarks>
public static class PanelKeys
{
    /// <summary>聊天面板（共享 ChatView，右 region）。</summary>
    public const string Chat = "chat";

    /// <summary>笔记面板（NotesView + AvaloniaEdit + form 节点两栏）。</summary>
    public const string Notes = "notes";

    /// <summary>专家面板（ExpertsView，6 tab）。</summary>
    public const string Experts = "experts";

    /// <summary>自动化面板（AutomationView，定时任务）。</summary>
    public const string Automation = "automation";

    /// <summary>测试面板（验证 IRegionManager 注入路径的占位 TextBlock）。</summary>
    public const string Test = "test";

    /// <summary>Stockapp 期货面板（FuturesView，CTP 行情/交易）。</summary>
    public const string Ctp = "ctp";

    /// <summary>Stockapp 回测/实盘面板（TradingView，Lean 引擎）。</summary>
    public const string Backtest = "backtest";

    /// <summary>Stockapp 股票交易面板（StockTradingView，FlaUI/MCP 路由）。</summary>
    public const string StockTrade = "stocktrade";

    /// <summary>插件注入演示 region 名（窗口底栏 ContentControl，经 IRegionManager.RegisterRegion 登记，接收 Test 面板）。</summary>
    public const string Inject = "inject";
}
