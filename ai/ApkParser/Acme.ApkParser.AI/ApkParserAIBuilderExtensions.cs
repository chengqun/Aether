using System.Reflection;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ApkParser.AI;

/// <summary>
/// DI entry point for the ApkParser AI adapter — 1 tool (GetApkInfo).
/// </summary>
public static class ApkParserAIBuilderExtensions
{
    public static readonly string[] ToolNames = { "GetApkInfo" };

    public static AetherAIBuilder AddApkParser(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddSingleton<ApkInfoExtractor>();
        s.AddTransient<ApkInfoTool>();
        s.AddSingleton<AIFunction>(sp => Create<ApkInfoTool>(sp, "GetApkInfo"));
        return builder;
    }

    private static AIFunction Create<T>(IServiceProvider sp, string method) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool);
    }
}
