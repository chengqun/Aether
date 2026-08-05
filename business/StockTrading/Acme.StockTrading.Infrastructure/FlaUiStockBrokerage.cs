using System.Diagnostics;
using System.Globalization;
using Acme.StockTrading.Domain;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using WpfClipboard = System.Windows.Clipboard;

namespace Acme.StockTrading.Infrastructure;

/// <summary>
/// <see cref="IStockBrokerage"/> 的 FlaUI 实现：操控同花顺「网上股票交易系统5.0」(xiadan.exe) 下单/查持仓。
/// 所有 FlaUI 类型只在本项目内可见 —— 上层只见 IStockBrokerage 端口。
///
/// 操控代码直接照搬已验证可跑通的参考项目 AiTrade/ViewModels/MainWindowViewModel.cs（买入/卖出/持仓均已 OK）：
///   - 每个操作 using new UIA3Automation() + GetDesktop().FindFirstChild(ByName("网上股票交易系统5.0"))，用完即弃，不缓存窗口引用；
///   - 切买入/卖出标签页：FindFirstDescendant(ByName("买入[F1]"/"卖出[F2]")) → Patterns.SelectionItem.Pattern.Select()；
///   - 下单表单 3 个 Edit：FindAllDescendants(ByControlType.Edit) 按序 = 代码/价格/数量，TextBox.Enter() 模拟键盘；
///   - 提交：FindFirstDescendant(ByName("买入"/"卖出")).AsButton().Invoke()；
///   - 持仓表自绘网格读不到文本 → 点击右下面板聚焦 → Ctrl+A/Ctrl+C → Clipboard.GetText() 解析 Tab 分隔行（ParseData）。
///
/// 唯一与参考项目的差异：参考是 WPF（UI 线程本身 STA），FlaUI/Clipboard 直接在 UI 线程调即可；
/// 本项目宿主是 Avalonia（UI 线程 MTA），故把每个 FlaUI 操作经 <see cref="RunSta"/> 切到 STA 后台线程执行。
/// </summary>
public sealed class FlaUiStockBrokerage : IStockBrokerage
{
    private readonly FlaUiStockBrokerageOptions _options;
    private readonly ILogger<FlaUiStockBrokerage>? _logger;

    private bool _connected;   // 仅标记「确认过窗口在」，不缓存窗口引用

    public FlaUiStockBrokerage(FlaUiStockBrokerageOptions options, ILogger<FlaUiStockBrokerage>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public event EventHandler<string>? LogReceived;

    private void Log(string message)
    {
        _logger?.LogInformation("FlaUI Brokerage: {Message}", message);
        LogReceived?.Invoke(this, message);
    }

    // ─────────────────────────── 连接（纯确认，秒回）───────────────────────────

    public Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default)
        => RunSta(ConnectCore);

    private (bool Success, string? Error) ConnectCore()
    {
        // 照搬参考：现取 automation + FindFirstChild(ByName)，using 即弃。在 → 标记已连接；不在 → 立即失败。
        using var automation = new UIA3Automation();
        var mainWindow = automation.GetDesktop().FindFirstChild(cf => cf.ByName(_options.ClientWindowTitle));
        if (mainWindow is null)
        {
            _connected = false;
            return (false, $"未找到标题为「{_options.ClientWindowTitle}」的窗口。请先打开并登录同花顺，或点「启动客户端」。");
        }
        _connected = true;
        Log($"已连接券商客户端：{mainWindow.Name}");
        return (true, (string?)null);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        Log("已断开（客户端保持运行）");
        return Task.CompletedTask;
    }

    public Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientExecutablePath))
            return Task.FromResult((false, "未配置客户端 exe 路径，请在「客户端配置」填写后保存重启"));
        try
        {
            Log($"启动客户端：{_options.ClientExecutablePath}");
            Process.Start(new ProcessStartInfo { FileName = _options.ClientExecutablePath!, UseShellExecute = true });
            Log("已发起启动，请手动登录同花顺，登录完成后点「连接」");
            return Task.FromResult((true, (string?)null));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, $"启动客户端失败：{ex.Message}"));
        }
    }

    // ─────────────────────────── 下单（照搬参考 ExecuteOrderAsync）───────────────────────────

    public Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default)
        => RunSta(() => PlaceOrderCore(request));

    private (bool Success, string? Error) PlaceOrderCore(StockOrderRequest request)
    {
        if (!_connected) return (false, "未连接，请先点「连接」");

        using var automation = new UIA3Automation();
        // 2. 查找主窗口
        var mainWindow = automation.GetDesktop().FindFirstChild(cf => cf.ByName(_options.ClientWindowTitle));
        if (mainWindow is null) return (false, "窗口未找到（客户端可能已关闭）");

        try
        {
            // 3. 切买入/卖出导航
            var isBuy = request.Direction == StockDirection.Buy;
            var daohang = isBuy ? "买入[F1]" : "卖出[F2]";
            var buyNav = mainWindow.FindFirstDescendant(cf => cf.ByName(daohang));
            if (buyNav == null) return (false, "买入导航未找到");
            var selectionItemPattern = buyNav.Patterns.SelectionItem.Pattern;
            if (selectionItemPattern == null) return (false, "SelectionItemPattern 未找到");
            selectionItemPattern.Select();

            // 4. 填写交易表单：3 个 Edit 按序 = 代码/价格/数量
            var edits = mainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
            if (edits.Length < 3) return (false, "交易表单未找到");

            var codeTextBox = edits[0].AsTextBox();
            var priceTextBox = edits[1].AsTextBox();
            var amtTextBox = edits[2].AsTextBox();

            codeTextBox.Enter(request.Symbol);
            Thread.Sleep(_options.StepDelayMs);
            priceTextBox.Enter(request.LimitPrice.ToString());
            Thread.Sleep(_options.StepDelayMs);
            amtTextBox.Enter(request.Quantity.ToString());
            Thread.Sleep(_options.StepDelayMs);

            // 5. 提交订单
            var buttonName = isBuy ? "买入" : "卖出";
            var submitButton = mainWindow.FindFirstDescendant(cf => cf.ByName(buttonName));
            if (submitButton == null) return (false, "提交按钮未找到");
            submitButton.AsButton().Invoke();

            Log($"已下单：{(isBuy ? "买入" : "卖出")} {request.Symbol} {request.Quantity}股 @ {request.LimitPrice}");
            return (true, (string?)null);
        }
        catch (Exception ex)
        {
            return (false, $"下单异常：{ex.Message}");
        }
    }

    // ─────────────────────────── 撤单（框架，按实际 UI 校准）───────────────────────────

    public Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default)
        => RunSta(() => CancelOrderCore(orderNo));

    private (bool Success, string? Error) CancelOrderCore(string orderNo)
    {
        if (!_connected) return (false, "未连接");
        if (string.IsNullOrWhiteSpace(orderNo)) return (false, "委托号为空");

        using var automation = new UIA3Automation();
        var mainWindow = automation.GetDesktop().FindFirstChild(cf => cf.ByName(_options.ClientWindowTitle));
        if (mainWindow is null) return (false, "窗口未找到");

        // 同花顺委托表撤单交互（双击行/右键/撤单标签页）需按实际 UI 校准，此处给框架。
        Log($"撤单请求：{orderNo}（委托表撤单交互需按实际客户端校准）");
        var nav = mainWindow.FindFirstDescendant(cf => cf.ByName("撤单[F3]"));
        nav?.Patterns.SelectionItem.Pattern?.Select();
        return (true, (string?)null);
    }

    // ─────────────────────────── 查持仓（照搬参考 LatestPositionAsync）───────────────────────────

    public Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default)
        => RunSta(QueryPositionsCore);

    private IReadOnlyList<StockPosition> QueryPositionsCore()
    {
        if (!_connected) return Array.Empty<StockPosition>();

        using var automation = new UIA3Automation();
        // 2. 查找主窗口
        var mainWindow = automation.GetDesktop().FindFirstChild(cf => cf.ByName(_options.ClientWindowTitle));
        if (mainWindow == null)
        {
            Log("查询持仓：窗口未找到");
            return Array.Empty<StockPosition>();
        }

        try
        {
            // 3. 点击买入导航（持仓面板在买入页可见）
            var buyNav = mainWindow.FindFirstDescendant(cf => cf.ByName("买入[F1]"));
            if (buyNav == null) return Array.Empty<StockPosition>();
            var selectionItemPattern = buyNav.Patterns.SelectionItem.Pattern;
            if (selectionItemPattern == null) return Array.Empty<StockPosition>();
            selectionItemPattern.Select();

            // 持仓面板在主窗口右下方区域（右侧约 2/3、底部约 1/4）
            var mainWindowRect = mainWindow.BoundingRectangle;
            var right = mainWindowRect.Right;
            var bottom = mainWindowRect.Bottom;
            var left = mainWindowRect.Left + (mainWindowRect.Width * 1 / 3);
            var top = mainWindowRect.Top + (mainWindowRect.Height * 3 / 4);

            // 聚焦到该区域（点击中心点）
            var centerX = (int)((left + right) / 2);
            var centerY = (int)((top + bottom) / 2);
            Mouse.MoveTo(centerX, centerY);
            Mouse.Click(MouseButton.Left);
            Thread.Sleep(100);

            // Ctrl+A 全选
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(100);

            // Ctrl+C 复制
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_C);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(1000);

            // 取剪贴板内容
            var clipboardText = WpfClipboard.GetText();
            if (string.IsNullOrEmpty(clipboardText))
            {
                Log("持仓：剪贴板内容为空");
                return Array.Empty<StockPosition>();
            }

            return ParsePositions(clipboardText);
        }
        catch (Exception ex)
        {
            Log($"查询持仓异常：{ex.Message}");
            return Array.Empty<StockPosition>();
        }
    }

    // ─────────────────────────── 持仓解析（照搬参考 ParseData 列序）───────────────────────────

    /// <summary>
    /// 解析同花顺持仓表剪贴板文本（Tab 分隔，首行表头）。列序与参考 ParseData 一致：
    /// 0 代码 / 1 名称 / 2 余额 / 3 可用 / 4 冻结 / 5 成本价 / 6 市价 / 7 盈亏 / 8 盈亏比 / 9 市值 / 10 持股天数 / 11 市场。
    /// </summary>
    private static IReadOnlyList<StockPosition> ParsePositions(string clipboardText)
    {
        var lines = clipboardText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return Array.Empty<StockPosition>();

        var list = new List<StockPosition>();
        for (int i = 1; i < lines.Length; i++) // 跳过表头
        {
            try
            {
                var fields = lines[i].Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 12) continue;

                list.Add(new StockPosition
                {
                    Symbol = fields[0],               // 证券代码
                    Name = fields[1],                 // 证券名称
                    Quantity = int.TryParse(fields[2], out int stockBalance) ? stockBalance : 0,
                    AvailableQuantity = int.TryParse(fields[3], out int availableBalance) ? availableBalance : 0,
                    FrozenQuantity = int.TryParse(fields[4], out int frozenQuantity) ? frozenQuantity : 0,
                    CostPrice = decimal.TryParse(fields[5], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal costPrice) ? costPrice : 0,
                    CurrentPrice = decimal.TryParse(fields[6], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal marketPrice) ? marketPrice : 0,
                    ProfitLoss = decimal.TryParse(fields[7], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal profitLoss) ? profitLoss : 0,
                    ProfitLossRatio = decimal.TryParse(fields[8], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal profitLossRatio) ? profitLossRatio : 0,
                    MarketValue = decimal.TryParse(fields[9], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal marketValue) ? marketValue : 0,
                    HoldingDays = int.TryParse(fields[10], out int holdingDays) ? holdingDays : 0,
                    ExchangeMarket = fields[11],      // 市场代码
                });
            }
            catch { continue; }
        }
        return list;
    }

    // ─────────────────────────── STA 线程切换 ───────────────────────────

    /// <summary>
    /// 把 FlaUI/Clipboard 操作切到 STA 后台线程（Avalonia UI 线程是 MTA，与参考 WPF 的 STA 不同）。
    /// </summary>
    private Task<T> RunSta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
