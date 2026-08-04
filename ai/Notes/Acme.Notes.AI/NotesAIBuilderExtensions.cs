using System.Reflection;
using Acme.Notes.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.AI;

public static class NotesAIBuilderExtensions
{
    public static readonly string[] ToolNames = {
        "CreateNote", "ReadNote", "AppendNote", "UpdateNote",
        "SearchNotes", "ListNotes", "DeleteNote",
        "ListKnowledgeObjects", "ReadKnowledgeObject"
    };

    /// <summary>Registers the Notes tools only. Business layers (Notes.Infrastructure/Application)
    /// are wired by the host composition root — the AI adapter must not reach into Infrastructure.</summary>
    public static AetherAIBuilder AddNotesTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddTransient<NoteTool>();
        s.AddTransient<KnowledgeTool>();

        const string cat = "笔记工具";
        const string kCat = "知识工具";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("CreateNote", "新建笔记", cat,
                sp => CreateTool<NoteTool>(sp, "CreateNote"), ToolApprovalLevel.Confirm);
            reg.Register("ReadNote", "读取笔记内容", cat,
                sp => CreateTool<NoteTool>(sp, "ReadNote"));
            reg.Register("AppendNote", "追加内容到笔记", cat,
                sp => CreateTool<NoteTool>(sp, "AppendNote"), ToolApprovalLevel.Confirm);
            reg.Register("UpdateNote", "更新笔记内容", cat,
                sp => CreateTool<NoteTool>(sp, "UpdateNote"), ToolApprovalLevel.Confirm);
            reg.Register("SearchNotes", "搜索笔记", cat,
                sp => CreateTool<NoteTool>(sp, "SearchNotes"));
            reg.Register("ListNotes", "列出笔记", cat,
                sp => CreateTool<NoteTool>(sp, "ListNotes"));
            reg.Register("DeleteNote", "删除笔记", cat,
                sp => CreateTool<NoteTool>(sp, "DeleteNote"), ToolApprovalLevel.Dangerous);
            reg.Register("ListKnowledgeObjects", "列出知识对象", kCat,
                sp => CreateTool<KnowledgeTool>(sp, "ListKnowledgeObjects"));
            reg.Register("ReadKnowledgeObject", "读取知识对象定义", kCat,
                sp => CreateTool<KnowledgeTool>(sp, "ReadKnowledgeObject"));
            reg.Register("CreateKnowledgeObject", "创建知识对象", kCat,
                sp => CreateTool<KnowledgeTool>(sp, "CreateKnowledgeObject"), ToolApprovalLevel.Confirm);
            reg.Register("UpdateKnowledgeObject", "更新知识对象", kCat,
                sp => CreateTool<KnowledgeTool>(sp, "UpdateKnowledgeObject"), ToolApprovalLevel.Confirm);
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
