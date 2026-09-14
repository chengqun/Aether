using System.Reflection;
using Acme.Qifu.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.AI;

/// <summary>
/// DI entry point for the qifu AI adapter. Registers tools only — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
///
/// 拆分（桌面-only vs 双端通用）：
/// - <see cref="AddQifuTools"/>：双端通用（毓数 SQL / 权限工单 / Teams 消息），纯 HTTP，手机+桌面都引。
/// - <see cref="AddQifuCloudDocsTools"/>：桌面-only（云文档登录/上传），依赖浏览器 OAuth + 本地文件夹，手机无浏览器不可用。
/// </summary>
public static class QifuAIBuilderExtensions
{
    /// <summary>双端通用工具名（毓数 SQL 9 + Teams 1，纯 HTTP）。</summary>
    public static readonly string[] ToolNames = {
        "AsyncSqlQuery", "FetchSqlTaskStatus", "PollSqlTaskStatus",
        "DownloadResult", "ReadResult", "CleanResults",
        "SqlLog", "ApplyModelUsageAuthority", "ApplyTableAuthority",
        "SendTeamsMessage"
    };

    /// <summary>桌面-only 云文档工具名（浏览器 OAuth + 本地文件夹上传）。</summary>
    public static readonly string[] CloudDocsToolNames = {
        "LoginCloudDocs", "CheckCloudDocsLogin", "UploadCloudDocsFolder",
        "UploadCloudDocsFile", "ListCloudDocsFolders"
    };

    /// <summary>注册双端通用 qifu 工具（毓数 SQL + 权限工单 + Teams）。手机/桌面都调此入口。</summary>
    public static AetherAIBuilder AddQifuTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<YushuSqlQueryTool>();
        services.AddTransient<TeamsMessageTool>();

        const string cat = "企服平台";
        builder.ConfigureToolRegistry(reg =>
        {
            // AsyncSqlQuery 提交任意 SQL 到毓数平台执行（可含 DROP/DELETE/UPDATE 等不可逆语句，
            // 同 ai/DataQuery 的 ExecuteSql 风险面）——必须显式标 Dangerous，否则 ToolRegistry 默认
            // Safe → 自动放行。（读结果/查状态的工具保持 Safe。）
            reg.Register("AsyncSqlQuery", "毓数异步 SQL 查询", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.AsyncSqlQuery)), ToolApprovalLevel.Dangerous);
            reg.Register("FetchSqlTaskStatus", "获取 SQL 任务状态", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.FetchSqlTaskStatus)));
            reg.Register("PollSqlTaskStatus", "轮询 SQL 任务状态", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.PollSqlTaskStatus)));
            reg.Register("DownloadResult", "下载查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.DownloadResult)));
            reg.Register("ReadResult", "读取查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.ReadResult)));
            reg.Register("CleanResults", "清理查询结果", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.CleanResults)), ToolApprovalLevel.Confirm);
            reg.Register("SqlLog", "拉取 SQL 任务执行日志", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.SqlLog)));
            reg.Register("ApplyModelUsageAuthority", "发起模型安全等级申请工单", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.ApplyModelUsageAuthority)), ToolApprovalLevel.Dangerous);
            reg.Register("ApplyTableAuthority", "发起表权限申请工单", cat,
                sp => CreateTool<YushuSqlQueryTool>(sp, nameof(YushuSqlQueryTool.ApplyTableAuthority)), ToolApprovalLevel.Dangerous);
            reg.Register("SendTeamsMessage", "发送 360Teams 消息", cat,
                sp => CreateTool<TeamsMessageTool>(sp, nameof(TeamsMessageTool.SendTeamsMessage)), ToolApprovalLevel.Dangerous);
        });
        return builder;
    }

    /// <summary>注册桌面-only 云文档工具（登录/上传/列目录）。依赖浏览器 OAuth + 本地文件夹，仅桌面调用。</summary>
    public static AetherAIBuilder AddQifuCloudDocsTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<CloudDocsUploadTool>();

        const string cat = "企服平台";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("LoginCloudDocs", "登录 360Teams 云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.LoginCloudDocs)));
            reg.Register("CheckCloudDocsLogin", "检查云文档登录状态", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.CheckCloudDocsLogin)));
            reg.Register("UploadCloudDocsFolder", "上传文件夹到云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFolder)), ToolApprovalLevel.Confirm);
            reg.Register("UploadCloudDocsFile", "上传文件到云文档", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.UploadCloudDocsFile)), ToolApprovalLevel.Confirm);
            reg.Register("ListCloudDocsFolders", "列出云文档目录", cat,
                sp => CreateTool<CloudDocsUploadTool>(sp, nameof(CloudDocsUploadTool.ListCloudDocsFolders)));
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
