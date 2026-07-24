using System.Reflection;
using Acme.Quotes.AI.Tools;
using Acme.Quotes.Domain;
using Acme.Securities.Domain;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Quotes.AI;

/// <summary>
/// DI entry point for the Quotes AI adapter. <see cref="AddQuotesTools"/> registers
/// the 2 tools + the Securities→Quotes ACL adapter (no agent); <see cref="AddQuotes"/>
/// adds the stock-analyst agent on top.
/// </summary>
public static class QuotesAIBuilderExtensions
{
    /// <summary>The 2 Quotes tool names.</summary>
    public static readonly string[] ToolNames = { "QuerySecurity", "CalculateIndicators" };

    /// <summary>Registers the Quotes tools + the Securities→Quotes ACL adapter (no agent).</summary>
    public static AetherAIBuilder AddQuotesTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        // ACL: bridge Securities into Quotes' ISecurityLookup port (Quotes itself has zero Securities dep).
        services.AddSingleton<ISecurityLookup>(sp => new SecurityLookupAdapter(sp.GetRequiredService<ISecurityManager>()));

        services.AddTransient<SecurityQueryTool>();
        services.AddTransient<TechnicalAnalysisTool>();
        services.AddSingleton<AIFunction>(sp => CreateTool<SecurityQueryTool>(sp, nameof(SecurityQueryTool.QuerySecurity)));
        services.AddSingleton<AIFunction>(sp => CreateTool<TechnicalAnalysisTool>(sp, nameof(TechnicalAnalysisTool.CalculateIndicators)));
        return builder;
    }

    /// <summary>Registers the Quotes tools + the stock-analyst agent.</summary>
    public static AetherAIBuilder AddQuotes(this AetherAIBuilder builder, string modelId)
    {
        AddQuotesTools(builder);
        builder.AddAgent("stock-analyst", a => a
            .Name("Stock Analyst")
            .Instructions(
                "你是股票/期货分析助手。可用工具：QuerySecurity（按关键词搜索证券代码/名称）、" +
                "CalculateIndicators（计算技术指标 SMA/EMA/RSI/MACD/BB/SAR/KDJ/ADX）。按需调用工具，简洁回复。")
            .ModelId(modelId)
            .WithTools(ToolNames));
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string methodName) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(method, tool);
    }
}
