using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Xunit;

namespace Acme.AppAutomation.Tests;

public class ThsProcedureTests
{
    [Fact]
    public async Task PlaceOrder_orchestrates_expected_primitive_sequence()
    {
        var desk = new RecordingDesktopAutomation();
        var proc = new ThsPlaceOrderProcedure();
        var args = new ThsPlaceOrderArgs("600000", 12.34m, 100, "buy");

        var result = await proc.ExecuteAsync(desk, args, default);

        Assert.True(result.Success);
        // 序列：聚焦主窗口 → 切买入tab → 点代码框 → 输代码 → tab → 输价 → tab → 输量 → 点提交 → 读回执
        Assert.Equal("focus:网上股票交易系统5.0", desk.Calls[0]);
        Assert.Equal("click:Name=买入[F1]", desk.Calls[1]);
        Assert.Equal("click:Role=textbox", desk.Calls[2]);
        Assert.Equal("type:600000", desk.Calls[3]);
        Assert.Equal("key:tab", desk.Calls[4]);
        Assert.Equal("type:12.34", desk.Calls[5]);
        Assert.Equal("key:tab", desk.Calls[6]);
        Assert.Equal("type:100", desk.Calls[7]);
        Assert.Equal("click:Name=买入", desk.Calls[8]);
        Assert.Equal("gettext:Name=确定", desk.Calls[9]);
    }

    [Fact]
    public async Task PlaceOrder_uses_sell_tab_when_direction_sell()
    {
        var desk = new RecordingDesktopAutomation();
        var proc = new ThsPlaceOrderProcedure();

        await proc.ExecuteAsync(desk, new ThsPlaceOrderArgs("000001", 10m, 200, "sell"), default);

        // 聚焦后切卖出 tab，提交按钮为「卖出」
        Assert.Equal("focus:网上股票交易系统5.0", desk.Calls[0]);
        Assert.Equal("click:Name=卖出[F2]", desk.Calls[1]);
        Assert.Contains("click:Name=卖出", desk.Calls);
    }

    [Fact]
    public async Task PlaceOrder_fails_without_args()
    {
        var desk = new RecordingDesktopAutomation();
        var proc = new ThsPlaceOrderProcedure();

        var result = await proc.ExecuteAsync(desk, null, default);

        Assert.False(result.Success);
        Assert.Empty(desk.Calls);
    }

    [Fact]
    public async Task Connect_checks_window_existence()
    {
        var desk = new RecordingDesktopAutomation { WindowExistsResult = true };
        var proc = new ThsConnectProcedure();

        var result = await proc.ExecuteAsync(desk, null, default);

        Assert.True(result.Success);
        Assert.Contains("focus:网上股票交易系统5.0", desk.Calls);
        Assert.Contains("exists:网上股票交易系统5.0", desk.Calls);
    }
}
