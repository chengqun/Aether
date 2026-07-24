using Acme.Notes.Application;
using Acme.Notes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Application.DependencyInjection;

public static class NotesApplicationExtensions
{
    public static IServiceCollection AddNotesApplication(this IServiceCollection services)
    {
        services.AddSingleton<INoteService, NoteService>();
        return services;
    }
}