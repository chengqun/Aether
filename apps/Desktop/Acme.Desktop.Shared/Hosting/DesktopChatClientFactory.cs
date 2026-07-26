using System.Collections.Concurrent;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// <see cref="IChatClientFactory"/> 的桌面实现：把专家的 ModelId（= LLM 配置 Id）
/// 解析为对应 provider 的 <see cref="IChatClient"/>。空/未命中 → 返回宿主默认 <see cref="IChatClient"/>。
/// 命中则用该配置的 ApiKey/Endpoint/ModelName 现建客户端并按 Id 缓存。
/// </summary>
public sealed class DesktopChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _defaultClient;
    private readonly ILlmConfigService _llmService;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private List<LlmConfigDto>? _configs;

    public DesktopChatClientFactory(IChatClient defaultClient, ILlmConfigService llmService)
    {
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
    }

    public IChatClient Create(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return _defaultClient;

        return _clients.GetOrAdd(modelId, id => BuildClient(id));
    }

    private IChatClient BuildClient(string configId)
    {
        var config = ResolveConfig(configId);
        if (config is null)
            return _defaultClient; // 配置被删除等：退化为默认

        OpenAIClient client = string.IsNullOrWhiteSpace(config.Endpoint)
            ? new OpenAIClient(config.ApiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) });
        return client.GetChatClient(config.ModelName).AsIChatClient();
    }

    private LlmConfigDto? ResolveConfig(string configId)
    {
        _configs ??= TryLoadConfigs();
        return _configs.FirstOrDefault(c => c.Id.ToString().Equals(configId, StringComparison.OrdinalIgnoreCase));
    }

    private List<LlmConfigDto> TryLoadConfigs()
    {
        try { return _llmService.GetCachedAsync().GetAwaiter().GetResult() ?? new(); }
        catch { return new(); }
    }
}
