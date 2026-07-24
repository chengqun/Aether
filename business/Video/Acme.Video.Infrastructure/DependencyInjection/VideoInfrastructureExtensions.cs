using System.IO;
using Acme.Video.Domain;
using Acme.Video.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Video.Infrastructure.DependencyInjection;

public static class VideoInfrastructureExtensions
{
    public static IServiceCollection AddVideoInfrastructure(this IServiceCollection services, string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("VIDEO_FEED_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "video.db");
        services.AddSingleton<IVideoFeedRepository>(_ => SqliteVideoFeedRepository.ForFile(dbPath));
        return services;
    }
}