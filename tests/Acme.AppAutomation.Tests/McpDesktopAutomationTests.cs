using System.Text.Json;
using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Xunit;

namespace Acme.AppAutomation.Tests;

public class McpDesktopAutomationTests
{
    [Fact]
    public async Task Click_maps_to_click_tool_with_locator_args()
    {
        var inv = new FakeMcpToolInvoker();
        var auto = new McpDesktopAutomation(inv);

        await auto.ClickAsync(DesktopLocator.ByName("下单"));

        var (tool, args) = Assert.Single(inv.Calls);
        Assert.Equal("click", tool);
        var dict = Assert.IsType<Dictionary<string, object?>>(args);
        Assert.Equal("name", dict["locator_kind"]);
        Assert.Equal("下单", dict["locator_value"]);
    }

    [Fact]
    public async Task Type_and_key_map_to_respective_tools()
    {
        var inv = new FakeMcpToolInvoker();
        var auto = new McpDesktopAutomation(inv);

        await auto.TypeAsync("600000");
        await auto.KeyAsync("tab");

        Assert.Equal("type", inv.Calls[0].Tool);
        Assert.Equal("key", inv.Calls[1].Tool);
    }

    [Fact]
    public async Task WindowExists_parses_result_json()
    {
        var inv = new FakeMcpToolInvoker { ReturnJson = """{"result":true}""" };
        var auto = new McpDesktopAutomation(inv);

        var exists = await auto.WindowExistsAsync("同花顺");

        Assert.True(exists);
        Assert.Equal("window_exists", inv.Calls[0].Tool);
    }

    [Fact]
    public async Task GetText_returns_text_field_from_json()
    {
        var inv = new FakeMcpToolInvoker { ReturnJson = """{"text":"已成交"}""" };
        var auto = new McpDesktopAutomation(inv);

        var text = await auto.GetTextAsync(DesktopLocator.ByName("回执"));

        Assert.Equal("已成交", text);
    }

    [Fact]
    public async Task IsConnected_delegates_to_invoker()
    {
        var inv = new FakeMcpToolInvoker { IsConnected = false };
        var auto = new McpDesktopAutomation(inv);
        Assert.False(auto.IsConnected);
    }
}
