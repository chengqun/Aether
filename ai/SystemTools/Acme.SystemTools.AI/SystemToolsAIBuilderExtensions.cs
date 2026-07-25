using System.Reflection;
using Acme.SystemTools.AI.Tools;
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

        s.AddSingleton<AIFunction>(sp => CreateTool<FileReadTool>(sp, "ReadFile"));
        s.AddSingleton<AIFunction>(sp => CreateTool<FileWriteTool>(sp, "WriteFile"));
        s.AddSingleton<AIFunction>(sp => CreateTool<FileEditTool>(sp, "EditFile"));
        s.AddSingleton<AIFunction>(sp => CreateTool<FileLineEditTool>(sp, "LineEditFile"));
        s.AddSingleton<AIFunction>(sp => CreateTool<FileDeleteTool>(sp, "DeleteFile"));
        s.AddSingleton<AIFunction>(sp => CreateTool<GrepTool>(sp, "Grep"));
        s.AddSingleton<AIFunction>(sp => CreateTool<GlobTool>(sp, "Glob"));
        s.AddSingleton<AIFunction>(sp => CreateTool<CommandExecutorTool>(sp, "ExecuteCommand"));
        s.AddSingleton<AIFunction>(sp => CreateTool<DirectoryExplorerTool>(sp, "ExploreDirectory"));
        s.AddSingleton<AIFunction>(sp => CreateTool<ShellCommandTool>(sp, "ExecuteShellCommand"));
        s.AddSingleton<AIFunction>(sp => CreateTool<WebFetchTool>(sp, "FetchUrl"));
        s.AddSingleton<AIFunction>(sp => CreateTool<FileDiffTool>(sp, "DiffFile"));

        builder.ConfigureToolRegistry(registry =>
        {
            const string cat = "系统工具";
            foreach (var name in ToolNames) registry.SetCategory(name, cat);
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
