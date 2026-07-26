using System.Reflection;
using Acme.ApkParser.AI.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ApkParser.AI;

/// <summary>
/// DI entry point for the ApkParser AI adapter. Registers the tool only — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
/// </summary>
public static class ApkParserAIBuilderExtensions
{
    public static readonly string[] ToolNames = { "GetApkInfo" };

    /// <summary>Registers the ApkParser tools only. Business layer is wired by the host.</summary>
    public static AetherAIBuilder AddApkParserTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddSingleton<ApkInfoExtractor>();
        s.AddTransient<ApkInfoTool>();

        const string cat = "APK 解析";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("GetApkInfo", "解析 APK 文件信息（包名/版本/权限/组件）", cat,
                sp => CreateTool<ApkInfoTool>(sp, "GetApkInfo"));
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string method) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool);
    }
}
