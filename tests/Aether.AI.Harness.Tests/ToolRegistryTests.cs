using System.ComponentModel;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void Register_and_Resolve_by_name()
    {
        var registry = new ToolRegistry();
        var tool = AIFunctionFactory.Create((Func<string, string>)GetWeather);

        registry.Register(tool);

        Assert.True(registry.Contains("GetWeather"));
        Assert.Same(tool, registry.Resolve("GetWeather"));
    }

    [Fact]
    public void ResolveAll_skips_missing_names()
    {
        var registry = new ToolRegistry();
        registry.RegisterFunction((Func<string, string>)GetWeather);

        var resolved = registry.ResolveAll(["GetWeather", "DoesNotExist"]);
        Assert.Single(resolved);
        Assert.Equal("GetWeather", resolved[0].Name);
    }

    [Fact]
    public void Remove_deletes_tool()
    {
        var registry = new ToolRegistry();
        registry.RegisterFunction((Func<string, string>)GetWeather);

        Assert.True(registry.Remove("GetWeather"));
        Assert.False(registry.Contains("GetWeather"));
    }

    [Description("Get the weather for a city.")]
    static string GetWeather([Description("The city.")] string city) => $"{city}: sunny";
}
