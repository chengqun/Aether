using System.Reflection;
using Acme.SystemTools.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
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
        "ExecuteShellCommand", "FetchUrl", "DiffFile"
    };

    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder)
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
        s.AddTransient<CommandExecutorTool>();
        s.AddTransient<DirectoryExplorerTool>();
        s.AddTransient<ShellCommandTool>();
        s.AddTransient<FileDiffTool>();

        const string cat = "系统工具";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("ReadFile", "读取文件内容（文本/图片）", cat,
                sp => CreateTool<FileReadTool>(sp, "ReadFile"));
            reg.Register("WriteFile", "写入文件内容", cat,
                sp => CreateTool<FileWriteTool>(sp, "WriteFile"), ToolApprovalLevel.Confirm);
            reg.Register("EditFile", "编辑文件（字符串替换）", cat,
                sp => CreateTool<FileEditTool>(sp, "EditFile"), ToolApprovalLevel.Confirm);
            reg.Register("LineEditFile", "按行编辑文件", cat,
                sp => CreateTool<FileLineEditTool>(sp, "LineEditFile"), ToolApprovalLevel.Confirm);
            reg.Register("DeleteFile", "删除文件", cat,
                sp => CreateTool<FileDeleteTool>(sp, "DeleteFile"), ToolApprovalLevel.Dangerous);
            reg.Register("MoveFile", "移动或重命名文件", cat,
                sp => CreateTool<FileMoveTool>(sp, "MoveFile"), ToolApprovalLevel.Confirm);
            reg.Register("Grep", "正则搜索文件内容", cat,
                sp => CreateTool<GrepTool>(sp, "Grep"));
            reg.Register("Glob", "按模式匹配文件（如 *.cs）", cat,
                sp => CreateTool<GlobTool>(sp, "Glob"));
            reg.Register("ExecuteCommand", "执行命令行程序", cat,
                sp => CreateTool<CommandExecutorTool>(sp, "ExecuteCommand"), ToolApprovalLevel.Dangerous);
            reg.Register("ExploreDirectory", "浏览目录结构", cat,
                sp => CreateTool<DirectoryExplorerTool>(sp, "ExploreDirectory"));
            reg.Register("ExecuteShellCommand", "执行 Shell 命令", cat,
                sp => CreateTool<ShellCommandTool>(sp, "ExecuteShellCommand"), ToolApprovalLevel.Dangerous);
            reg.Register("FetchUrl", "抓取网页内容", cat,
                sp => CreateTool<WebFetchTool>(sp, "FetchUrl"));
            reg.Register("DiffFile", "比较文件差异", cat,
                sp => CreateTool<FileDiffTool>(sp, "DiffFile"));
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
