using Acme.Notes.Application;
using Acme.Notes.Application.Catalog;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Application.DependencyInjection;

public static class NotesApplicationExtensions
{
    public static IServiceCollection AddNotesApplication(this IServiceCollection services)
    {
        services.AddSingleton<INoteService, NoteService>();
        // 笔记单元来源端口：默认 SQLite 源（行为与改前一致）；目录源（labs，Infrastructure 的 DirectoryNoteCatalogSource）
        // 由宿主按需换注册——切 INoteCatalogSource 这一行即可回滚，不触碰批量消费逻辑。
        services.AddSingleton<INoteCatalogSource, SqliteNoteCatalogSource>();
        return services;
    }
}