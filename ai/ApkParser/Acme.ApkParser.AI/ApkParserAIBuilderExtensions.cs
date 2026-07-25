using System.Reflection;
using Acme.ApkParser.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ApkParser.AI;

/// <summary>
/// DI entry point for the ApkParser AI adapter.
/// <see cref="AddApkParserTools"/> registers the tool (no agent);
/// <see cref="AddApkParser"/> adds the apk-parser agent on top.
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
        s.AddSingleton<AIFunction>(sp => CreateTool<ApkInfoTool>(sp, "GetApkInfo"));

        builder.ConfigureToolRegistry(registry =>
        {
            const string cat = "APK 解析";
            foreach (var name in ToolNames) registry.SetCategory(name, cat);
        });
        return builder;
    }

    /// <summary>Registers the ApkParser tools + the apk-parser agent.</summary>
    public static AetherAIBuilder AddApkParser(this AetherAIBuilder builder, string modelId)
    {
        AddApkParserTools(builder);
        builder.AddAgent("apk-parser", a => a
            .Name("APK Parser")
            .Instructions(
                "你是 APK 解析助手。可用工具：GetApkInfo（解析 APK 文件包名、版本、权限、组件等信息）。" +
                "按需调用工具，简洁回复结果。")
            .ModelId(modelId)
            .WithTools(ToolNames));
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
