using System.IO;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;
using Acme.Notes.Infrastructure;
using Acme.Notes.Infrastructure.Agents;
using Acme.Notes.Infrastructure.Catalog;
using Acme.Notes.Infrastructure.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Infrastructure.DependencyInjection;

public static class NotesInfrastructureExtensions
{
    public static IServiceCollection AddNotesInfrastructure(this IServiceCollection services, string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("NOTES_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "notes.db");
        services.AddSingleton<INoteRepository>(_ => SqliteNoteRepository.ForFile(dbPath));
        services.AddSingleton<IAgentSourceParser, AgentSourceParser>();
        services.AddSingleton<IRuleSourceParser, RuleSourceParser>();
        services.AddSingleton<ICodeSourceParser, CodeSourceParser>();
        services.AddSingleton<IWorkflowSourceParser, WorkflowSourceParser>();
        services.AddSingleton<IKnowledgeSourceParser, KnowledgeSourceParser>();
        // 文档版（.md 文件）知识对象解析器：targetId 为相对路径字符串（非 note 版 Guid?），独立 DTO + 解析器
        services.AddSingleton<IDocumentKnowledgeSourceParser, DocumentKnowledgeSourceParser>();
        // 目录源（labs）实现：不注册为 INoteCatalogSource（默认是 Application 的 SQLite 源），只按具体类型注册，
        // 由宿主在「打开 labs 目录」时用当前根目录换注册 INoteCatalogSource。根目录经 Func<string?> 惰性注入。
        services.AddSingleton<DirectoryNoteCatalogSource>(sp => new DirectoryNoteCatalogSource(null));
        return services;
    }
}
