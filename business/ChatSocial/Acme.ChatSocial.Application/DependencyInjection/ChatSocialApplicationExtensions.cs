using Acme.ChatSocial.Application;
using Acme.ChatSocial.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ChatSocial.Application.DependencyInjection;

public static class ChatSocialApplicationExtensions
{
    public static IServiceCollection AddChatSocialApplication(this IServiceCollection services)
    {
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IRelationshipService, RelationshipService>();
        services.AddSingleton<IGroupService, GroupService>();
        return services;
    }
}