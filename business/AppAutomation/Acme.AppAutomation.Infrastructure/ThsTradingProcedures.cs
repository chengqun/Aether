using System.Globalization;
using Acme.AppAutomation.Application;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// 同花顺「网上股票交易系统5.0」(xiadan.exe) 的固定操作流程。locator 值已用 FlaUI-MCP 的
/// <c>windows_snapshot</c> 在真实客户端上校准过（2026-08）：导航是 <c>treeitem</c>（买入[F1]/卖出[F2]/撤单[F3]），
/// 提交按钮 <c>button "买入"/"卖出"</c>。3 个表单 textbox 的 Name 是动态显示值（代码/名称/价格），
/// 无稳定 Name/AutomationId，故用「点代码框（ByRole textbox 取首个）→ Type → Tab 跳价框 → Type → Tab 跳量框」序列。
/// <para>真实树片段（买入页）：<c>- textbox "[1032]" (代码) / - textbox "浦发银行" (名称) / - textbox "20200" (价格)
/// / - button "买入" / - button "重填"</c>。价格/数量框靠 Tab 跳转定位，不逐个 locator。</para>
/// 这些过程把「操作同花顺界面」固化成代码——区别于 LLM 直接调 MCP 工具（不固定）。
/// 过程不引 StockTrading 类型（args 用本文件的 record），保持 AppAutomation 通用。
/// </summary>
public static class ThsLocators
{
    /// <summary>主窗口标题（<see cref="IStockTradingOptions.ClientWindowTitle"/> 默认值）。连接时聚焦此窗口。</summary>
    public const string MainWindowTitle = "网上股票交易系统5.0";

    // 导航 treeitem：FlaUI-MCP snapshot 里 role=treeitem，Name 稳定（买入[F1]/卖出[F2]/撤单[F3]）。点按即可切换。
    public static readonly DesktopLocator MainWindow = DesktopLocator.ByName(MainWindowTitle);
    public static readonly DesktopLocator BuyTab = DesktopLocator.ByName("买入[F1]");
    public static readonly DesktopLocator SellTab = DesktopLocator.ByName("卖出[F2]");
    public static readonly DesktopLocator CancelTab = DesktopLocator.ByName("撤单[F3]");

    /// <summary>代码输入框（买入页首个 textbox；Name 是动态占位符如 "[1032]"，故按 role 取首个）。</summary>
    public static readonly DesktopLocator SymbolField = DesktopLocator.ByRole("textbox");

    // 提交按钮：button "买入"/"卖出"（Name 稳定，已校准）。下单过程按方向选。
    public static DesktopLocator SubmitButton(string direction) =>
        DesktopLocator.ByName(direction == "sell" ? "卖出" : "买入");

    // 持仓：切查询[F4]→资金股票 treeitem。MCP 路径用 get_text 取面板文本（bridge 的 ThsPositionsParser 解析）。
    public static readonly DesktopLocator PositionsNav = DesktopLocator.ByName("查询[F4]");
    public static readonly DesktopLocator PositionsItem = DesktopLocator.ByName("资金股票");
    /// <summary>下单回执。TODO: 同花顺点「买入」后弹确认对话框，回执元素需用 snapshot 校准（当前占位）。</summary>
    public static readonly DesktopLocator Receipt = DesktopLocator.ByName("确定");
}

/// <summary>下单参数（AppAutomation 侧，与 StockTrading 解耦；桥接器把 StockOrderRequest 映射过来）。</summary>
public sealed record ThsPlaceOrderArgs(string Symbol, decimal Price, int Quantity, string Direction);

/// <summary>撤单参数。</summary>
public sealed record ThsCancelArgs(string OrderNo);

/// <summary>确认同花顺窗口在不在（不启动、不阻塞）。</summary>
public sealed class ThsConnectProcedure : Procedure
{
    public override string Name => "ths_connect";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        await d.FocusWindowAsync(ThsLocators.MainWindow.Value, ct).ConfigureAwait(false);
        var exists = await d.WindowExistsAsync(ThsLocators.MainWindow.Value, ct).ConfigureAwait(false);
        return exists ? ProcedureResult.Ok() : ProcedureResult.Fail("未找到同花顺窗口（请启动客户端并登录）");
    }
}

/// <summary>启动同花顺 exe：由宿主（<c>AppAutomationStockBrokerage</c>）直接 <c>Process.Start</c>，不经 MCP server。
/// 此过程保留为占位（不在 bridge 调用），真正的启动逻辑在 bridge 的 <c>LaunchClientAsync</c>。</summary>
public sealed class ThsLaunchProcedure : Procedure
{
    public override string Name => "ths_launch";
    public override Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
        => Task.FromResult(ProcedureResult.Fail("启动客户端由宿主直接 Process.Start 处理，不走 MCP server"));
}

/// <summary>下单：聚焦主窗口 → 切买/卖 tab → 点代码框 → 输代码 → Tab → 输价 → Tab → 输量 → 点提交 → 读回执。</summary>
public sealed class ThsPlaceOrderProcedure : Procedure
{
    public override string Name => "ths_place_order";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        if (args is not ThsPlaceOrderArgs a)
            return ProcedureResult.Fail("参数缺失：需要 ThsPlaceOrderArgs");

        await d.FocusWindowAsync(ThsLocators.MainWindowTitle, ct).ConfigureAwait(false);
        await d.ClickAsync(a.Direction == "sell" ? ThsLocators.SellTab : ThsLocators.BuyTab, ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.SymbolField, ct).ConfigureAwait(false);
        await d.TypeAsync(a.Symbol, ct).ConfigureAwait(false);
        await d.KeyAsync("tab", ct).ConfigureAwait(false);
        await d.TypeAsync(a.Price.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await d.KeyAsync("tab", ct).ConfigureAwait(false);
        await d.TypeAsync(a.Quantity.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.SubmitButton(a.Direction), ct).ConfigureAwait(false);
        var receipt = await d.GetTextAsync(ThsLocators.Receipt, ct).ConfigureAwait(false);
        return ProcedureResult.Ok(receipt);
    }
}

/// <summary>撤单：切撤单 tab → 选中委托行（TODO 按委托号定位行）→ 点撤单。</summary>
public sealed class ThsCancelProcedure : Procedure
{
    public override string Name => "ths_cancel";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        if (args is not ThsCancelArgs a)
            return ProcedureResult.Fail("参数缺失：需要 ThsCancelArgs");
        await d.FocusWindowAsync(ThsLocators.MainWindowTitle, ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.CancelTab, ct).ConfigureAwait(false);
        // TODO(Windows): 按委托号 a.OrderNo 定位委托行 → 点撤单。委托表结构需用 get_ui_tree 探明后补。
        return ProcedureResult.Ok();
    }
}

/// <summary>查持仓：切查询[F4]→资金股票 → 取面板文本（桥接器解析成 StockPosition[]）。</summary>
public sealed class ThsQueryPositionsProcedure : Procedure
{
    public override string Name => "ths_query_positions";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        await d.FocusWindowAsync(ThsLocators.MainWindowTitle, ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.PositionsNav, ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.PositionsItem, ct).ConfigureAwait(false);
        await Task.Delay(300, ct);
        // 资金股票面板文本：用 get_ui_tree 取整个面板树（bridge 的 ThsPositionsParser 按 Tab 行解析）。
        var tree = await d.GetUITreeAsync(ct: ct).ConfigureAwait(false);
        return ProcedureResult.Ok(tree);
    }
}

/// <summary>断开（同花顺无显式断开；过程空操作，桥接器据此清 IsConnected）。</summary>
public sealed class ThsDisconnectProcedure : Procedure
{
    public override string Name => "ths_disconnect";
    public override Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
        => Task.FromResult(ProcedureResult.Ok());
}
