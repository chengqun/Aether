using System.Reflection;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.SystemTools.AI;

/// <summary>
/// DI entry point for the SystemTools AI adapter — 12 file/shell/web tools.
/// Stateless tools (no DI deps); register + expose as AIFunctions.
/// </summary>
public static class SystemToolsAIBuilderExtensions
{
    public static readonly string[] ToolNames = {
        "ReadFile", "WriteFile", "EditFile", "LineEditFile", "DeleteFile",
        "Grep", "Glob", "ExecuteCommand", "ExploreDirectory",
        "ExecuteShellCommand", "FetchUrl", "DiffFile"
    };

    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddTransient<FileReadTool>();
        s.AddTransient<FileWriteTool>();
        s.AddTransient<FileEditTool>();
        s.AddTransient<FileLineEditTool>();
        s.AddTransient<FileDeleteTool>();
        s.AddTransient<GrepTool>();
        s.AddTransient<GlobTool>();
        s.AddTransient<CommandExecutorTool>();
        s.AddTransient<DirectoryExplorerTool>();
        s.AddTransient<ShellCommandTool>();
        s.AddTransient<WebFetchTool>();
        s.AddTransient<FileDiffTool>();

        s.AddSingleton<AIFunction>(sp => Create<FileReadTool>(sp, "ReadFile"));
        s.AddSingleton<AIFunction>(sp => Create<FileWriteTool>(sp, "WriteFile"));
        s.AddSingleton<AIFunction>(sp => Create<FileEditTool>(sp, "EditFile"));
        s.AddSingleton<AIFunction>(sp => Create<FileLineEditTool>(sp, "LineEditFile"));
        s.AddSingleton<AIFunction>(sp => Create<FileDeleteTool>(sp, "DeleteFile"));
        s.AddSingleton<AIFunction>(sp => Create<GrepTool>(sp, "Grep"));
        s.AddSingleton<AIFunction>(sp => Create<GlobTool>(sp, "Glob"));
        s.AddSingleton<AIFunction>(sp => Create<CommandExecutorTool>(sp, "ExecuteCommand"));
        s.AddSingleton<AIFunction>(sp => Create<DirectoryExplorerTool>(sp, "ExploreDirectory"));
        s.AddSingleton<AIFunction>(sp => Create<ShellCommandTool>(sp, "ExecuteShellCommand"));
        s.AddSingleton<AIFunction>(sp => Create<WebFetchTool>(sp, "FetchUrl"));
        s.AddSingleton<AIFunction>(sp => Create<FileDiffTool>(sp, "DiffFile"));
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
