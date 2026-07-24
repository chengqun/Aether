using Acme.Notes.Application;
using Acme.Notes.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Application.DependencyInjection;

public static class NotesApplicationExtensions
{
    public static IServiceCollection AddNotesApplication(this IServiceCollection services, string? sqliteDbPath = null)
    {
        services.AddNotesInfrastructure(sqliteDbPath);
        services.AddSingleton<INoteService, NoteService>();
        return services;
    }
}