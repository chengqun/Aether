using System.IO;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;
using Acme.Notes.Infrastructure;
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
        services.AddSingleton<IKnowledgeSourceParser, KnowledgeSourceParser>();
        return services;
    }
}
