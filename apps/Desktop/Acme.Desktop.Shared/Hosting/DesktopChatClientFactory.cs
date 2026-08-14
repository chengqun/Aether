using System.Collections.Concurrent;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// <see cref="IChatClientFactory"/> 的桌面实现。agent 的 ModelId 为空时（主流做法：agent 不绑模型），
/// 用 <see cref="SelectedLlmHolder"/> 持有的聊天页当前选中 LLM；未选则退化为宿主默认。
/// </summary>
public sealed class DesktopChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _defaultClient;
    private readonly ILlmConfigService _llmService;
    private readonly SelectedLlmHolder _llmHolder;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private List<LlmConfigDto>? _configs;

    public DesktopChatClientFactory(IChatClient defaultClient, ILlmConfigService llmService, SelectedLlmHolder llmHolder)
    {
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _llmHolder = llmHolder ?? throw new ArgumentNullException(nameof(llmHolder));
    }

    public IChatClient Create(string? modelId)
    {
        // agent 不绑模型 → 用聊天页选中的 LLM
        if (string.IsNullOrWhiteSpace(modelId))
        {
            var selectedId = _llmHolder.LlmConfigId;
            if (!string.IsNullOrWhiteSpace(selectedId))
                return _clients.GetOrAdd(selectedId, id => BuildClient(id));
            return Wrap(_defaultClient, "desktop-default");
        }

        return _clients.GetOrAdd(modelId, id => BuildClient(id));
    }

    private IChatClient BuildClient(string configId)
    {
        var config = ResolveConfig(configId);
        if (config is null)
            return Wrap(_defaultClient, "desktop-default"); // 配置被删除等：退化为默认

        OpenAIClient client = string.IsNullOrWhiteSpace(config.Endpoint)
            ? new OpenAIClient(config.ApiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) });
        return Wrap(client.GetChatClient(config.ModelName).AsIChatClient(), "desktop");
    }

    /// <summary>临时诊断包装（调查工具/ask_user 重复执行两遍）：记录每次 LLM 响应里的工具调用。定位后移除。</summary>
    private static IChatClient Wrap(IChatClient inner, string label)
        => new ToolCallDiagnosticChatClient(inner, label);

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
}
