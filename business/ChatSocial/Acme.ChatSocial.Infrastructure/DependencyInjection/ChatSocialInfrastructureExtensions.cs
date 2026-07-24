using Microsoft.Extensions.DependencyInjection;

namespace Acme.ChatSocial.Infrastructure.DependencyInjection;

public static class ChatSocialInfrastructureExtensions
{
    public static IServiceCollection AddChatSocialInfrastructure(this IServiceCollection services)
    {
        return services;
    }
}