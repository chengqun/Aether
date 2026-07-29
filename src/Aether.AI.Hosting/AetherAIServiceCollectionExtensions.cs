using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Aether.AI.Hosting;

/// <summary>
/// DI registration entry point for the Aether.AI platform. Calling
/// <see cref="AddAetherAI"/> wires the Microsoft Agent Framework chat client,
/// the Harness runtime, session/tool management, and the Memory layer.
/// </summary>
public static class AetherAIServiceCollectionExtensions
{
    /// <summary>
    /// Adds Aether.AI platform services to <paramref name="services"/> and
    /// returns a builder for registering agents and tools.
    /// </summary>
    public static AetherAIBuilder AddAetherAI(this IServiceCollection services)
    {
        services.AddOptions();

        // Options
        services.AddSingleton<AetherAIOptions>(sp => sp.GetRequiredService<IOptions<AetherAIOptions>>().Value);

        // Chat client (OpenAI / Azure OpenAI). Built lazily on first resolve so
        // that a missing API key does not crash DI composition.
        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<AetherAIOptions>();
            var apiKey = string.IsNullOrWhiteSpace(options.OpenAI.ApiKey)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                : options.OpenAI.ApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "No OpenAI API key configured. Set AetherAI:OpenAI:ApiKey or the OPENAI_API_KEY environment variable.");
            }

            // Wires an OpenAI-compatible chat client. When an Endpoint is set
            // (e.g. DeepSeek, Azure-compatible, local), it is used; otherwise the
            // public OpenAI service is used. Azure OpenAI can be substituted by
            // replacing this with an AzureOpenAIClient in a later phase.
            OpenAIClient client = string.IsNullOrWhiteSpace(options.OpenAI.Endpoint)
                ? new OpenAIClient(apiKey)
                : new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(options.OpenAI.Endpoint!) });
            return client.GetChatClient(options.OpenAI.ModelId).AsIChatClient();
        });

        // Tools — ToolRegistry collects metadata (name/description/category) + factory
        // from each adapter's IToolRegistryContributor. Factories are only invoked
        // when resolving tools for an agent, so GetAllMetas() never instantiates
        // AIFunction and never fails due to missing DI dependencies.
        services.AddSingleton<ToolRegistry>(sp => new ToolRegistry(sp));

        // Skills — SkillRegistry 收集各适配器通过 ISkillRegistryContributor 注册的提示词级 skill。
        services.AddSingleton<SkillRegistry>(sp => new SkillRegistry(sp));

        // Harness
        services.AddSingleton<IChatClientFactory, DefaultChatClientFactory>();
        services.AddSingleton<AgentFactory>(sp =>
        {
            var factory = new AgentFactory(
                sp.GetRequiredService<IChatClientFactory>(),
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetRequiredService<SkillRegistry>(),
                sp.GetService<ILoggerFactory>());
            factory.UseChatClientAgent = sp.GetRequiredService<AetherAIOptions>().UseChatClientAgent;
            factory.FileMemoryRoot = sp.GetRequiredService<AetherAIOptions>().FileMemoryRoot;
            return factory;
        });
        services.AddSingleton<SessionManager>();
        services.AddSingleton<HarnessRuntime>(sp =>
        {
            var runtime = new HarnessRuntime(
                sp.GetRequiredService<AgentFactory>(),
                sp.GetRequiredService<SessionManager>());
            // Register every agent definition contributed via the builder.
            foreach (var definition in sp.GetServices<AgentDefinition>())
            {
                runtime.RegisterAgent(definition);
            }
            return runtime;
        });

        // Memory (Phase 1 ships InMemory backends only)
        services.AddSingleton<IMemoryStore, InMemoryMemoryStore>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<MemoryService>();

        // 会话标题自动生成（LLM，首轮后异步补；无 key 时退化为 no-op）
        services.AddSingleton<IConversationTitleService, ConversationTitleService>();

        return new AetherAIBuilder(services);
    }
}
