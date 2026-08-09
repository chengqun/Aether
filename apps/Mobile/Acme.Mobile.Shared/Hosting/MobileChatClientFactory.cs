using System.Collections.Concurrent;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Acme.Mobile.Shared.Hosting;

/// <summary>
/// <see cref="IChatClientFactory"/> 的手机实现。逻辑与桌面 <c>DesktopChatClientFactory</c> 一致
/// （UI 无关）：agent 的 ModelId 为空时用 <see cref="SelectedLlmHolder"/> 持有的当前选中 LLM；
/// 未选则退化为宿主默认。新增 <see cref="ClearCache"/> 供 LLM 配置变更后失效缓存。
/// </summary>
public sealed class MobileChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _defaultClient;
    private readonly ILlmConfigService _llmService;
    private readonly SelectedLlmHolder _llmHolder;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private List<LlmConfigDto>? _configs;

    public MobileChatClientFactory(IChatClient defaultClient, ILlmConfigService llmService, SelectedLlmHolder llmHolder)
    {
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _llmHolder = llmHolder ?? throw new ArgumentNullException(nameof(llmHolder));
    }

    public IChatClient Create(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            var selectedId = _llmHolder.LlmConfigId;
            if (!string.IsNullOrWhiteSpace(selectedId))
                return _clients.GetOrAdd(selectedId, id => BuildClient(id));
            return _defaultClient;
        }
        return _clients.GetOrAdd(modelId, id => BuildClient(id));
    }

    private IChatClient BuildClient(string configId)
    {
        var config = ResolveConfig(configId);
        if (config is null) return _defaultClient;

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
        try { return Task.Run(() => _llmService.GetCachedAsync()).GetAwaiter().GetResult() ?? new(); }
        catch { return new(); }
    }

    /// <summary>LLM 配置变更后失效缓存（配置列表 + 已建客户端），下轮 Create 重建。</summary>
    public void ClearCache()
    {
        _configs = null;
        // 丢弃已建客户端（Close 不可 await， DisposeAsyncClient 与平台版本差异大，直接清字典重建即可）。
        _clients.Clear();
    }
}
