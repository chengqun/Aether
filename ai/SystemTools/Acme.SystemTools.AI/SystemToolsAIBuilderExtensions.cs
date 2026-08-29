using System.Reflection;
using Acme.SystemTools.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

namespace Acme.SystemTools.AI;

/// <summary>
/// DI entry point for the SystemTools AI adapter — 12 file/shell/web tools.
/// Registers tool metadata + factory into ToolRegistry (no AIFunction instantiated
/// at DI time; factory invoked lazily when an agent resolves its tools).
/// </summary>
public static class SystemToolsAIBuilderExtensions
{
    public static readonly string[] ToolNames = {
        "ReadFile", "WriteFile", "EditFile", "LineEditFile", "DeleteFile", "MoveFile",
        "Grep", "Glob", "ExecuteCommand", "ExploreDirectory",
        "ExecuteShellCommand", "FetchUrl", "DiffFile",
        "ApplyPatch", "ApplyPatchWithDelete"
    };

    /// <summary>命令执行类工具名（ExecuteCommand / ExecuteShellCommand）——spawn 子进程，
    /// 在 iOS 真机被沙盒禁止（App 不允许 fork），故手机端跳过。</summary>
    public static readonly string[] CommandToolNames = { "ExecuteCommand", "ExecuteShellCommand" };

    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder)
        => AddSystemTools(builder, includeCommandTools: true);

    /// <summary>
    /// 注册系统工具（文件/搜索/HTTP/命令）。
    /// <paramref name="includeCommandTools"/>=false 时跳过 ExecuteCommand/ExecuteShellCommand 两个
    /// spawn 子进程的工具——手机端（iOS 沙盒禁止 fork）用此入口只拿文件/搜索/HTTP 的可移植子集。
    /// </summary>
    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder, bool includeCommandTools)
    {
        var s = builder.Services;
        s.AddHttpClient();
        s.AddTransient<WebFetchTool>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new WebFetchTool(factory.CreateClient());
        });
        s.AddTransient<FileReadTool>();
        s.AddTransient<FileWriteTool>();
        s.AddTransient<FileEditTool>();
        s.AddTransient<FileLineEditTool>();
        s.AddTransient<FileDeleteTool>();
        s.AddTransient<FileMoveTool>();
        s.AddTransient<GrepTool>();
        s.AddTransient<GlobTool>();
        if (includeCommandTools) s.AddTransient<CommandExecutorTool>();
        s.AddTransient<DirectoryExplorerTool>();
        if (includeCommandTools) s.AddTransient<ShellCommandTool>();
        s.AddTransient<FileDiffTool>();
        s.AddTransient<ApplyPatchTool>();

        const string cat = "系统工具";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("ReadFile", "读取文件内容（文本/图片）", cat,
                sp => CreateTool<FileReadTool>(sp, "ReadFile"));
            reg.Register("WriteFile", "写入文件内容", cat,
                sp => CreateTool<FileWriteTool>(sp, "WriteFile")); // Safe：自动跑 + 事后 diff + 一键撤销
            reg.Register("EditFile", "编辑文件（字符串替换）", cat,
                sp => CreateTool<FileEditTool>(sp, "EditFile")); // Safe：自动跑 + 事后 diff + 一键撤销
            reg.Register("LineEditFile", "按行编辑文件", cat,
                sp => CreateTool<FileLineEditTool>(sp, "LineEditFile")); // Safe：自动跑 + 事后 diff + 一键撤销
            reg.Register("DeleteFile", "删除文件", cat,
                sp => CreateTool<FileDeleteTool>(sp, "DeleteFile"), ToolApprovalLevel.Dangerous);
            reg.Register("MoveFile", "移动或重命名文件", cat,
                sp => CreateTool<FileMoveTool>(sp, "MoveFile"), ToolApprovalLevel.Confirm);
            reg.Register("Grep", "正则搜索文件内容", cat,
                sp => CreateTool<GrepTool>(sp, "Grep"));
            reg.Register("Glob", "按模式匹配文件（如 *.cs）", cat,
                sp => CreateTool<GlobTool>(sp, "Glob"));
            reg.Register("ExploreDirectory", "浏览目录结构", cat,
                sp => CreateTool<DirectoryExplorerTool>(sp, "ExploreDirectory"));
            reg.Register("FetchUrl", "抓取网页内容", cat,
                sp => CreateTool<WebFetchTool>(sp, "FetchUrl"));
            reg.Register("DiffFile", "比较文件差异", cat,
                sp => CreateTool<FileDiffTool>(sp, "DiffFile"));
            // 多文件批量编辑：纯 create/edit/lineEdit → Safe（自动跑 + 事后 diff + 一键撤销，同单文件工具）。
            // 含 delete 的批次走 ApplyPatchWithDelete（Dangerous，审批兜底）——delete 不可逆、无内容 diff。
            reg.Register("ApplyPatch", "批量编辑多个文件（create/edit/lineEdit，不含删除）", cat,
                sp => CreateTool<ApplyPatchTool>(sp, "ApplyPatch")); // Safe：自动跑 + 多文件事后 diff + 独立撤销
            reg.Register("ApplyPatchWithDelete", "批量编辑多个文件（含删除，危险）", cat,
                sp => CreateTool<ApplyPatchTool>(sp, "ApplyPatchWithDelete"), ToolApprovalLevel.Dangerous);

            if (includeCommandTools)
            {
                reg.Register("ExecuteCommand", "执行命令行程序", cat,
                    sp => CreateTool<CommandExecutorTool>(sp, "ExecuteCommand"), ToolApprovalLevel.Dangerous);
                reg.Register("ExecuteShellCommand", "执行 Shell 命令", cat,
                    sp => CreateTool<ShellCommandTool>(sp, "ExecuteShellCommand"), ToolApprovalLevel.Dangerous);
            }
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
