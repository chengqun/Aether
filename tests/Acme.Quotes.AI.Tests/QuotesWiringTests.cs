using Acme.Quotes.AI;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Infrastructure.DependencyInjection;
using Acme.Securities.Application.DependencyInjection;
using Acme.Securities.Infrastructure.DependencyInjection;
using Aether.AI.Hosting;
using Aether.AI.Harness;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Quotes.AI.Tests;

public class QuotesWiringTests
{
    [Fact]
    public void AddQuotes_registers_agent_and_two_tools()
    {
        var builder = Host.CreateApplicationBuilder();
        var aether = builder.Services.AddAetherAI();
        aether.Configure(o => { o.OpenAI.ApiKey = "offline"; o.OpenAI.ModelId = "offline"; });
        builder.Services.AddSecurityInfrastructure();
        builder.Services.AddSecurityApplication();
        builder.Services.AddQuotesInfrastructure();
        builder.Services.AddQuotesApplication();
        aether.AddQuotes("offline");

        using var app = builder.Build();
        var runtime = app.Services.GetRequiredService<HarnessRuntime>();
        var registry = app.Services.GetRequiredService<ToolRegistry>();

        Assert.True(runtime.HasAgent("stock-analyst"));
        Assert.True(registry.Contains("QuerySecurity"));
        Assert.True(registry.Contains("CalculateIndicators"));
    }
}

