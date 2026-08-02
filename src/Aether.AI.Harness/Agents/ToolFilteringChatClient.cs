using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatClient"/> 包装器：从 <see cref="ChatOptions.Tools"/> 中过滤掉指定工具名，
/// 使 LLM 看不到这些工具（不会调用），但不影响 MAF 内部的 provider 状态管理。
/// <para>用途：<see cref="AgentModeProvider"/> 的 get_mode/set_mode 工具导致 GLM 无限调用——
/// 过滤掉后 LLM 不再看到这些工具，但 AgentModeProvider 仍然注入模式上下文 + 管理 mode 状态。</para>
/// </summary>
public sealed class ToolFilteringChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly HashSet<string> _hiddenToolNames;

    public ToolFilteringChatClient(IChatClient inner, params string[] hiddenToolNames)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hiddenToolNames = new HashSet<string>(hiddenToolNames, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, FilterTools(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, FilterTools(options), cancellationToken);

    private ChatOptions? FilterTools(ChatOptions? options)
    {
        if (options?.Tools is null || options.Tools.Count == 0) return options;

        var filtered = options.Tools
            .Where(t => t is not AIFunction fn || !_hiddenToolNames.Contains(fn.Name))
            .ToList();

        if (filtered.Count == options.Tools.Count) return options; // 无变化

        // 创建新的 ChatOptions（只替换 Tools，其余属性复制）
        return new ChatOptions
        {
            Instructions = options.Instructions,
            Tools = filtered,
            Temperature = options.Temperature,
            TopP = options.TopP,
            MaxOutputTokens = options.MaxOutputTokens,
            ResponseFormat = options.ResponseFormat,
            StopSequences = options.StopSequences,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            ToolMode = options.ToolMode,
            AdditionalProperties = options.AdditionalProperties,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
