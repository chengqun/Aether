using Acme.Ctp.AI;
using Acme.Ctp.Application.DependencyInjection;
using Acme.Ctp.Infrastructure;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Ctp.AI.Tests;

public class CtpWiringTests
{
    [Fact]
    public void AddCtpTools_registers_seven_tools()
    {
        var builder = Host.CreateApplicationBuilder();
        var aether = builder.Services.AddAetherAI();
        aether.Configure(o => { o.OpenAI.ApiKey = "offline"; o.OpenAI.ModelId = "offline"; });
        builder.Services.AddCtpInfrastructure();
        builder.Services.AddCtpApplication();
        aether.AddCtpTools();

        using var app = builder.Build();
        var registry = app.Services.GetRequiredService<ToolRegistry>();

        // The 7 CTP AI functions must be in the tool registry.
        string[] expected = {
            "GetStatus", "QueryAccount", "QueryPositions",
            "OrderInsert", "OrderAction",
            "QueryLatestTick", "QueryAllLatestTicks"
        };
        foreach (var name in expected)
            Assert.True(registry.Contains(name), $"missing CTP tool: {name}");
    }
}
