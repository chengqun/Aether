using System.Text.Json;
using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Xunit;

namespace Acme.AppAutomation.Tests;

public class McpDesktopAutomationTests
{
    // FlaUI-MCP snapshot 行格式：- role "name" [ref=wXeY]
    private const string SampleTree =
        "- window \"网上股票交易系统5.0\" [ref=w1]\n" +
        "  - tablist [ref=w1e1]\n" +
        "    - tab \"买入[F1]\" [ref=w1e2]\n" +
        "    - tab \"卖出[F2]\" [ref=w1e3]\n" +
        "  - textbox [ref=w1e10]\n" +
        "  - button \"买入\" [ref=w1e20]\n";

    /// <summary>读匿名参数对象的属性值（McpDesktopAutomation 传匿名对象给 invoker）。</summary>
    private static string? Arg(object? args, string name)
    {
        if (args is null) return null;
        var prop = args.GetType().GetProperty(name);
        return prop?.GetValue(args)?.ToString();
    }

    [Fact]
    public async Task Click_snapshots_then_clicks_resolved_ref()
    {
        var inv = new FakeMcpToolInvoker();
        inv.ReturnsByTool["windows_snapshot"] = SampleTree;
        var auto = new McpDesktopAutomation(inv);

        await auto.ClickAsync(DesktopLocator.ByName("买入[F1]"));

        // 序列：windows_snapshot → windows_click {ref=w1e2}
        Assert.Equal("windows_snapshot", inv.Calls[0].Tool);
        Assert.Equal("windows_click", inv.Calls[1].Tool);
        Assert.Equal("w1e2", Arg(inv.Calls[1].Args, "ref"));
    }

    [Fact]
    public async Task Click_by_role_matches_snapshot_role()
    {
        var inv = new FakeMcpToolInvoker();
        inv.ReturnsByTool["windows_snapshot"] = SampleTree;
        var auto = new McpDesktopAutomation(inv);

        await auto.ClickAsync(DesktopLocator.ByRole("textbox"));

        Assert.Equal("windows_click", inv.Calls[1].Tool);
        Assert.Equal("w1e10", Arg(inv.Calls[1].Args, "ref"));
    }

    [Fact]
    public async Task DoubleClick_and_right_click_set_button_doubleClick_flags()
    {
        var inv = new FakeMcpToolInvoker();
        inv.ReturnsByTool["windows_snapshot"] = SampleTree;
        var auto = new McpDesktopAutomation(inv);

        await auto.DoubleClickAsync(DesktopLocator.ByName("买入"));
        await auto.RightClickAsync(DesktopLocator.ByName("买入"));

        Assert.Equal("left", Arg(inv.Calls[1].Args, "button"));
        Assert.Equal("True", Arg(inv.Calls[1].Args, "doubleClick"));
        Assert.Equal("right", Arg(inv.Calls[3].Args, "button"));
    }

    [Fact]
    public async Task Click_throws_when_element_not_in_snapshot()
    {
        var inv = new FakeMcpToolInvoker { ReturnJson = SampleTree };
        var auto = new McpDesktopAutomation(inv);

        // snapshot 返回的树里没有「不存在的元素」
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auto.ClickAsync(DesktopLocator.ByName("不存在的元素")));
    }

    [Fact]
    public async Task Type_maps_to_windows_type_with_text()
    {
        var inv = new FakeMcpToolInvoker();
        var auto = new McpDesktopAutomation(inv);

        await auto.TypeAsync("600000");

        Assert.Equal("windows_type", inv.Calls[0].Tool);
        Assert.Equal("600000", Arg(inv.Calls[0].Args, "text"));
    }

    [Fact]
    public async Task Key_maps_to_windows_send_keys_with_chord_conversion()
    {
        var inv = new FakeMcpToolInvoker();
        var auto = new McpDesktopAutomation(inv);

        await auto.KeyAsync("ctrl+a");
        await auto.KeyAsync("tab");

        Assert.Equal("windows_send_keys", inv.Calls[0].Tool);
        Assert.Equal("Ctrl+A", Arg(inv.Calls[0].Args, "chord"));
        Assert.Equal("Tab", Arg(inv.Calls[1].Args, "chord"));
    }

    [Fact]
    public async Task WindowExists_lists_windows_and_matches_title()
    {
        var inv = new FakeMcpToolInvoker();
        inv.ReturnsByTool["windows_list_windows"] =
            "- w1: \"网上股票交易系统5.0\" (xiadan)\n- w2: \"记事本\" (notepad)";
        var auto = new McpDesktopAutomation(inv);

        var exists = await auto.WindowExistsAsync("网上股票交易系统5.0");

        Assert.True(exists);
        Assert.Equal("windows_list_windows", inv.Calls[0].Tool);
    }

    [Fact]
    public async Task GetText_snapshots_then_gets_text_by_ref()
    {
        var inv = new FakeMcpToolInvoker();
        inv.ReturnsByTool["windows_snapshot"] = SampleTree;
        inv.ReturnsByTool["windows_get_text"] = "已成交";
        var auto = new McpDesktopAutomation(inv);

        var text = await auto.GetTextAsync(DesktopLocator.ByName("买入"));

        Assert.Equal("已成交", text);
        Assert.Equal("windows_snapshot", inv.Calls[0].Tool);
        Assert.Equal("windows_get_text", inv.Calls[1].Tool);
        Assert.Equal("w1e20", Arg(inv.Calls[1].Args, "ref"));
    }

    [Fact]
    public async Task FocusWindow_calls_windows_focus_with_title()
    {
        var inv = new FakeMcpToolInvoker();
        var auto = new McpDesktopAutomation(inv);

        await auto.FocusWindowAsync("网上股票交易系统5.0");

        Assert.Equal("windows_focus", inv.Calls[0].Tool);
        Assert.Equal("网上股票交易系统5.0", Arg(inv.Calls[0].Args, "title"));
    }

    [Fact]
    public void FindRefInTree_finds_by_name_and_role()
    {
        var refByName = McpDesktopAutomation.FindRefInTree(SampleTree, DesktopLocator.ByName("卖出[F2]"));
        Assert.Equal("w1e3", refByName);

        var refByRole = McpDesktopAutomation.FindRefInTree(SampleTree, DesktopLocator.ByRole("tablist"));
        Assert.Equal("w1e1", refByRole);

        Assert.Null(McpDesktopAutomation.FindRefInTree(SampleTree, DesktopLocator.ByName("不存在")));
    }

    [Fact]
    public void ToChord_normalizes_key_combos()
    {
        Assert.Equal("Ctrl+A", McpDesktopAutomation.ToChord("ctrl+a"));
        Assert.Equal("Tab", McpDesktopAutomation.ToChord("tab"));
        Assert.Equal("Enter", McpDesktopAutomation.ToChord("enter"));
        Assert.Equal("Ctrl+Shift+S", McpDesktopAutomation.ToChord("ctrl+shift+s"));
    }

    [Fact]
    public async Task IsConnected_delegates_to_invoker()
    {
        var inv = new FakeMcpToolInvoker { IsConnected = false };
        var auto = new McpDesktopAutomation(inv);
        Assert.False(auto.IsConnected);
    }
}
