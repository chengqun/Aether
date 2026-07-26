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

        const string cat = "企服平台";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("LoginCloudDocs", "登录 360Teams 云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.LoginCloudDocs)));
            reg.Register("CheckCloudDocsLogin", "检查云文档登录状态", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.CheckCloudDocsLogin)));
            reg.Register("UploadCloudDocsFolder", "上传文件夹到云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFolder)));
            reg.Register("UploadCloudDocsFile", "上传文件到云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFile)));
            reg.Register("ListCloudDocsFolders", "列出云文档目录", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.ListCloudDocsFolders)));
            reg.Register("AsyncSqlQuery", "毓数异步 SQL 查询", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.AsyncSqlQuery)));
            reg.Register("FetchSqlTaskStatus", "获取 SQL 任务状态", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.FetchSqlTaskStatus)));
            reg.Register("PollSqlTaskStatus", "轮询 SQL 任务状态", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.PollSqlTaskStatus)));
            reg.Register("DownloadResult", "下载查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.DownloadResult)));
            reg.Register("ReadResult", "读取查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.ReadResult)));
            reg.Register("CleanResults", "清理查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.CleanResults)));
            reg.Register("SendTeamsMessage", "发送 360Teams 消息", cat,
                sp => CreateTool<TeamsMessageTool>(sp, nameof(TeamsMessageTool.SendTeamsMessage)));
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
