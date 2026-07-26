using Acme.Quotes.AI;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Infrastructure.DependencyInjection;
using Acme.Securities.Application.DependencyInjection;
using Acme.Securities.Infrastructure.DependencyInjection;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Quotes.AI.Tests;

public class QuotesWiringTests
{
    [Fact]
    public void AddQuotesTools_registers_two_tools()
    {
        var builder = Host.CreateApplicationBuilder();
        var aether = builder.Services.AddAetherAI();
        aether.Configure(o => { o.OpenAI.ApiKey = "offline"; o.OpenAI.ModelId = "offline"; });
        builder.Services.AddSecurityInfrastructure();
        builder.Services.AddSecurityApplication();
        builder.Services.AddQuotesInfrastructure();
        builder.Services.AddQuotesApplication();
        aether.AddQuotesTools();

        using var app = builder.Build();
        var registry = app.Services.GetRequiredService<ToolRegistry>();

        Assert.True(registry.Contains("QuerySecurity"));
        Assert.True(registry.Contains("CalculateIndicators"));
    }
}

