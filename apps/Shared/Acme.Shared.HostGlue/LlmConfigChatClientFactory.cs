using System.Collections.Concurrent;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Acme.Shared.HostGlue;

/// <summary>
/// <see cref="IChatClientFactory"/> 的共享宿主实现（UI 无关，桌面/手机共用）。
/// agent 的 ModelId 为空时（主流做法：agent 不绑模型），用 <see cref="SelectedLlmHolder"/> 持有的
/// 聊天页当前选中 LLM；未选则退化为宿主默认客户端。
/// </summary>
/// <remarks>
/// 合并自原 <c>DesktopChatClientFactory</c> 与 <c>MobileChatClientFactory</c>（两者 BuildClient 逻辑逐字相同）。
/// 差异通过 <paramref name="clientWrapper"/> 参数化：桌面传 null（不包层），手机传
/// <c>c => new ToolHistorySanitizingChatClient(c, "mobile")</c>（修孤儿 tool 消息防 400）。包装层同时作用于
/// 默认客户端与按配置构建的客户端，以保留手机两端都包的行为。
/// </remarks>
public sealed class LlmConfigChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _defaultClient;
    private readonly ILlmConfigService _llmService;
    private readonly SelectedLlmHolder _llmHolder;
    private readonly Func<IChatClient, IChatClient>? _clientWrapper;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private List<LlmConfigDto>? _configs;

    public LlmConfigChatClientFactory(
        IChatClient defaultClient,
        ILlmConfigService llmService,
        SelectedLlmHolder llmHolder,
        Func<IChatClient, IChatClient>? clientWrapper = null)
    {
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _llmHolder = llmHolder ?? throw new ArgumentNullException(nameof(llmHolder));
        _clientWrapper = clientWrapper;
    }

    public IChatClient Create(string? modelId)
    {
        // agent 不绑模型 → 用聊天页选中的 LLM
        if (string.IsNullOrWhiteSpace(modelId))
        {
            var selectedId = _llmHolder.LlmConfigId;
            if (!string.IsNullOrWhiteSpace(selectedId))
                return _clients.GetOrAdd(selectedId, id => BuildClient(id));
            return Wrap(_defaultClient);
        }

        return _clients.GetOrAdd(modelId, id => BuildClient(id));
    }

    private IChatClient BuildClient(string configId)
    {
        var config = ResolveConfig(configId);
        if (config is null)
            return Wrap(_defaultClient); // 配置被删除等：退化为默认

        OpenAIClient client = string.IsNullOrWhiteSpace(config.Endpoint)
            ? new OpenAIClient(config.ApiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) });
        return Wrap(client.GetChatClient(config.ModelName).AsIChatClient());
    }

    /// <summary>套用可选包装层（手机端 ToolHistorySanitizingChatClient；桌面 null 直通）。</summary>
    private IChatClient Wrap(IChatClient inner)
        => _clientWrapper is null ? inner : _clientWrapper(inner);

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
        // 丢弃已建客户端（Close 不可 await，DisposeAsyncClient 与平台版本差异大，直接清字典重建即可）。
        _clients.Clear();
    }
}
