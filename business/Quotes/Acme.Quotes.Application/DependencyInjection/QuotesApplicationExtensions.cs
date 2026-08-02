using Acme.Quotes.Application;
using Acme.Quotes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Quotes.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Quotes Application layer — the analysis manager
/// (cache-first kline fetch + indicator calc). Consumes Security's
/// <c>ISecurityRepository</c> (one-way Quotes→Security). Call alongside
/// <c>AddQuotesInfrastructure</c> + <c>AddSecurityInfrastructure</c>.
/// </summary>
public static class QuotesApplicationExtensions
{
    public static IServiceCollection AddQuotesApplication(this IServiceCollection services)
    {
        services.AddSingleton<IAnalysisManager, AnalysisManager>();
        services.AddSingleton<IAnalysisService>(sp => sp.GetRequiredService<IAnalysisManager>() as IAnalysisService
            ?? throw new InvalidOperationException("AnalysisManager must implement IAnalysisService"));
        return services;
    }
}
