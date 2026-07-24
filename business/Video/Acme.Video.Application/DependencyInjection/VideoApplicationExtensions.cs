using Acme.Video.Domain;
using Acme.Video.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Video.Application.DependencyInjection;

public static class VideoApplicationExtensions
{
    public static IServiceCollection AddVideoApplication(this IServiceCollection services, string? sqliteDbPath = null)
    {
        services.AddVideoInfrastructure(sqliteDbPath);
        services.AddSingleton<IVideoFeedService, VideoFeedService>();
        return services;
    }
}