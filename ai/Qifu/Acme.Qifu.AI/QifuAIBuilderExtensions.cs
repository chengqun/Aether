using System.Reflection;
using Acme.Qifu.AI.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.AI;

/// <summary>
/// DI entry point for the qifu AI adapter. Registers the 12 tools only — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
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

    private static AIFunction CreateTool<T>(IServiceProvider sp, string methodName) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(method, tool);
    }
}
