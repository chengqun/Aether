using System.Globalization;
using Acme.AppAutomation.Application;
using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Infrastructure;

/// <summary>
/// 同花顺下单客户端的固定操作流程（locator 占位，Windows 期用 FlaUI-MCP/Accessibility Inspector 探出真实值）。
/// 这些过程把「操作同花顺界面」固化成代码——区别于 LLM 直接调 MCP 工具（不固定）。
/// 过程不引 StockTrading 类型（args 用本文件的 record），保持 AppAutomation 通用。
/// </summary>
public static class ThsLocators
{
    // TODO(Windows): 以下 locator 全是占位，需在 Windows 上探同花顺「网上股票交易系统5.0」真实元素后填入。
    public static readonly DesktopLocator MainWindow = DesktopLocator.ByName("网上股票交易系统5.0");
    public static readonly DesktopLocator BuyTab = DesktopLocator.ByName("买入");
    public static readonly DesktopLocator SellTab = DesktopLocator.ByName("卖出");
    public static readonly DesktopLocator SymbolField = DesktopLocator.ByAutomationId("TODO:代码输入框");
    public static readonly DesktopLocator SubmitButton = DesktopLocator.ByName("下单");
    public static readonly DesktopLocator PositionsTab = DesktopLocator.ByName("持仓");
    public static readonly DesktopLocator PositionsGrid = DesktopLocator.ByAutomationId("TODO:持仓表格");
    public static readonly DesktopLocator OrderListTab = DesktopLocator.ByName("委托");
    public static readonly DesktopLocator Receipt = DesktopLocator.ByName("TODO:回执");
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

/// <summary>启动同花顺 exe（TODO Windows：接 MCP server 的 launch_application 工具或由宿主直接启动）。</summary>
public sealed class ThsLaunchProcedure : Procedure
{
    public override string Name => "ths_launch";
    public override Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
        => Task.FromResult(ProcedureResult.Fail("TODO(Windows): 接 launch_application 工具或宿主直接启动 exe"));
}

/// <summary>下单：切买/卖 tab → 输代码 → Tab → 输价 → Tab → 输量 → 点下单 → 读回执。</summary>
public sealed class ThsPlaceOrderProcedure : Procedure
{
    public override string Name => "ths_place_order";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        if (args is not ThsPlaceOrderArgs a)
            return ProcedureResult.Fail("参数缺失：需要 ThsPlaceOrderArgs");

        await d.ClickAsync(a.Direction == "sell" ? ThsLocators.SellTab : ThsLocators.BuyTab, ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.SymbolField, ct).ConfigureAwait(false);
        await d.TypeAsync(a.Symbol, ct).ConfigureAwait(false);
        await d.KeyAsync("tab", ct).ConfigureAwait(false);
        await d.TypeAsync(a.Price.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await d.KeyAsync("tab", ct).ConfigureAwait(false);
        await d.TypeAsync(a.Quantity.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await d.ClickAsync(ThsLocators.SubmitButton, ct).ConfigureAwait(false);
        var receipt = await d.GetTextAsync(ThsLocators.Receipt, ct).ConfigureAwait(false);
        return ProcedureResult.Ok(receipt);
    }
}

/// <summary>撤单（TODO Windows：选中委托行 → 点撤单）。</summary>
public sealed class ThsCancelProcedure : Procedure
{
    public override string Name => "ths_cancel";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        if (args is not ThsCancelArgs a)
            return ProcedureResult.Fail("参数缺失：需要 ThsCancelArgs");
        await d.ClickAsync(ThsLocators.OrderListTab, ct).ConfigureAwait(false);
        // TODO(Windows): 按委托号定位行 → 点撤单
        await d.ClickAsync(DesktopLocator.ByName($"TODO:委托行:{a.OrderNo}"), ct).ConfigureAwait(false);
        return ProcedureResult.Ok();
    }
}

/// <summary>查持仓：切持仓 tab → 取 UI 树（桥接器解析成 StockPosition[]）。</summary>
public sealed class ThsQueryPositionsProcedure : Procedure
{
    public override string Name => "ths_query_positions";
    public override async Task<ProcedureResult> ExecuteAsync(IDesktopAutomation d, object? args, CancellationToken ct)
    {
        await d.ClickAsync(ThsLocators.PositionsTab, ct).ConfigureAwait(false);
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
