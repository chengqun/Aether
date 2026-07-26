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

        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        Assert.True(registry.Contains("GetWeather"));
        var resolved = registry.ResolveAll(["GetWeather"]);
        Assert.Single(resolved);
        Assert.Equal("GetWeather", resolved[0].Name);
    }

    [Fact]
    public void ResolveAll_skips_missing_names()
    {
        var registry = new ToolRegistry();
        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        var resolved = registry.ResolveAll(["GetWeather", "DoesNotExist"]);
        Assert.Single(resolved);
        Assert.Equal("GetWeather", resolved[0].Name);
    }

    [Fact]
    public void GetAllMetas_returns_category_and_description()
    {
        var registry = new ToolRegistry();
        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        var metas = registry.GetAllMetas();

        Assert.Single(metas);
        Assert.Equal("GetWeather", metas[0].Name);
        Assert.Equal("test", metas[0].Category);
        Assert.Equal("Get the weather for a city.", metas[0].Description);
    }

    [Description("Get the weather for a city.")]
    static string GetWeather([Description("The city.")] string city) => $"{city}: sunny";
}
