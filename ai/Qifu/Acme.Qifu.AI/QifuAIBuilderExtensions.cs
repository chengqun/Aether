using System.Reflection;
using Acme.Qifu.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.AI;

/// <summary>
/// DI entry point for the qifu AI adapter. <see cref="AddQifuTools"/> registers
/// the 12 tools (no agent); <see cref="AddQifu"/> adds the qifu-assistant agent on
/// top. A host that wants one combined agent with tools from many contexts calls
/// <c>AddQifuTools</c> (+ other contexts' AddXxxTools) and registers its own agent.
/// </summary>
public static class QifuAIBuilderExtensions
{
    /// <summary>The 12 qifu tool names (C# method names; MEAI strips no suffix here).</summary>
    public static readonly string[] ToolNames = {
        "LoginCloudDocs", "CheckCloudDocsLogin", "UploadCloudDocsFolder",
        "UploadCloudDocsFile", "ListCloudDocsFolders",
        "AsyncSqlQuery", "FetchSqlTaskStatus", "PollSqlTaskStatus",
        "DownloadResult", "ReadResult", "CleanResults",
        "SendTeamsMessage"
    };

    /// <summary>Registers the qifu tool classes + their AIFunctions (no agent).</summary>
    public static AetherAIBuilder AddQifuTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<CloudDocsUploadTool>();
        services.AddTransient<YushuSqlQueryTool>();
        services.AddTransient<TeamsMessageTool>();

        services.AddSingleton<AIFunction>(sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.LoginCloudDocs)));
        services.AddSingleton<AIFunction>(sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.CheckCloudDocsLogin)));
        services.AddSingleton<AIFunction>(sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFolder)));
        services.AddSingleton<AIFunction>(sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFile)));
        services.AddSingleton<AIFunction>(sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.ListCloudDocsFolders)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.AsyncSqlQuery)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.FetchSqlTaskStatus)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.PollSqlTaskStatus)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.DownloadResult)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.ReadResult)));
        services.AddSingleton<AIFunction>(sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.CleanResults)));
        services.AddSingleton<AIFunction>(sp => CreateTool<TeamsMessageTool>(sp, nameof(TeamsMessageTool.SendTeamsMessage)));

        builder.ConfigureToolRegistry(registry =>
        {
            const string cat = "企服平台";
            foreach (var name in ToolNames) registry.SetCategory(name, cat);
        });
        return builder;
    }

    /// <summary>Registers the qifu tools + the qifu-assistant agent.</summary>
    public static AetherAIBuilder AddQifu(this AetherAIBuilder builder, string modelId)
    {
        AddQifuTools(builder);
        builder.AddAgent("qifu-assistant", a => a
            .Name("Qifu Assistant")
            .Instructions(
                "你是 qifu 内部平台集成助手。可用工具：毓数异步 SQL 查询/状态/下载/预览/清理、" +
                "360Teams 云文档登录/上传/列目录、360Teams 消息通知。按需调用工具完成用户请求，简洁回复。")
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
