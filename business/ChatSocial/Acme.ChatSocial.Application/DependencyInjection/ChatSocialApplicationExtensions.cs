using Acme.ChatSocial.Domain;
using Acme.ChatSocial.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ChatSocial.Application.DependencyInjection;

public static class ChatSocialApplicationExtensions
{
    public static IServiceCollection AddChatSocialApplication(this IServiceCollection services)
    {
        services.AddChatSocialInfrastructure();
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IRelationshipService, RelationshipService>();
        services.AddSingleton<IGroupService, GroupService>();
        services.AddSingleton<IMomentService, MomentService>();
        return services;
    }
}