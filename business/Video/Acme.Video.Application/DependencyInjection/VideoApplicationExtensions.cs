using Acme.Video.Application;
using Acme.Video.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Video.Application.DependencyInjection;

public static class VideoApplicationExtensions
{
    public static IServiceCollection AddVideoApplication(this IServiceCollection services)
    {
        services.AddSingleton<IVideoFeedService, VideoFeedService>();
        return services;
    }
}