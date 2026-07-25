using System.Reflection;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.AI;

public static class NotesAIBuilderExtensions
{
    public static readonly string[] ToolNames = {
        "CreateNote", "ReadNote", "AppendNote", "UpdateNote",
        "SearchNotes", "ListNotes", "DeleteNote"
    };

    /// <summary>Registers the Notes tools only. Business layers (Notes.Infrastructure/Application)
    /// are wired by the host composition root — the AI adapter must not reach into Infrastructure.</summary>
    public static AetherAIBuilder AddNotesTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddTransient<NoteTool>();
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "CreateNote"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "ReadNote"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "AppendNote"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "UpdateNote"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "SearchNotes"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "ListNotes"));
        s.AddSingleton<AIFunction>(sp => CreateTool<NoteTool>(sp, "DeleteNote"));
        return builder;
    }

    public static AetherAIBuilder AddNotes(this AetherAIBuilder builder, string modelId)
    {
        AddNotesTools(builder);
        builder.AddAgent("notes-assistant", a => a
            .Name("Notes Assistant")
            .Instructions(
                "你是本地 Markdown 笔记助手。可用工具：新建/读取/追加/更新/搜索/列出/删除笔记。" +
                "按用户意图调用合适的工具完成请求，简洁回复结果。")
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