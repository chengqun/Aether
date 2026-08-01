using Aether.AI.Core.Memory;
using Aether.AI.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 把 <see cref="IMemoryStore"/>（长期记忆）接入 agent 的 <see cref="AIContextProvider"/>：
/// <list type="bullet">
/// <item><see cref="ProvideAIContextAsync"/>：每轮 run 前召回当前用户的长期记忆，作为
///   <see cref="AIContext.Instructions"/> 注入（跨会话保留的用户偏好/事实/任务历史）。</item>
/// <item><see cref="StoreAIContextAsync"/>：每轮 run 后把本轮 Q&amp;A 沉淀为一条
///   <see cref="MemoryType.TaskHistory"/> 记忆项，供后续召回。</item>
/// </list>
/// 区别于会话内聊天历史窗口（<c>SlidingWindowChatReducer</c> 等）——这是跨会话的提炼记忆。
/// <para>userId 运行时由 <see cref="_userIdAccessor"/> 解析，避免缓存 agent 在登录/登出后失效。</para>
/// </summary>
public sealed class MemoryContextProvider : AIContextProvider
{
    private readonly IMemoryStore _store;
    private readonly Func<string?> _userIdAccessor;
    private readonly ILoggerFactory? _loggerFactory;

    public MemoryContextProvider(IMemoryStore store, Func<string?> userIdAccessor, ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _userIdAccessor = userIdAccessor ?? throw new ArgumentNullException(nameof(userIdAccessor));
        _loggerFactory = loggerFactory;
    }

    /// <summary>召回长期记忆并注入为 system 指令。无记忆时返回空 <see cref="AIContext"/>（不改变行为）。</summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        try
        {
            var block = await RecallInstructionsAsync(_userIdAccessor(), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(block) ? new AIContext() : new AIContext { Instructions = block };
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogWarning(ex, "召回长期记忆失败，跳过注入。");
            return new AIContext(); // 记忆失败不阻断主流程
        }
    }

    /// <summary>把本轮 Q&amp;A 沉淀为一条 TaskHistory 记忆项。失败静默（不阻断 run 完成）。</summary>
    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken)
    {
        if (context.InvokeException is not null) return; // 失败的 run 不沉淀

        try
        {
            var userId = _userIdAccessor();
            var (question, answer) = ExtractTurn(context.RequestMessages, context.ResponseMessages);
            var item = BuildEpisodicMemory(userId, question, answer);
            if (item is null) return;
            await _store.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogWarning(ex, "沉淀长期记忆失败。");
        }
    }

    /// <summary>召回并格式化为注入指令块；无记忆返回 null。</summary>
    public async Task<string?> RecallInstructionsAsync(string? userId, CancellationToken cancellationToken)
    {
        var items = await _store.FindByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (items is null || items.Count == 0) return null;
        return FormatMemoryBlock(items);
    }

    /// <summary>从本轮请求/响应消息提取用户提问与 agent 回答。</summary>
    public static (string? question, string answer) ExtractTurn(
        IEnumerable<ChatMessage>? requestMessages, IEnumerable<ChatMessage>? responseMessages)
        => (ExtractLastUserText(requestMessages), ExtractAssistantText(responseMessages));

    /// <summary>构造一条 TaskHistory 记忆项；无有效提问时返回 null。</summary>
    public static MemoryItem? BuildEpisodicMemory(string? userId, string? question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var content = string.IsNullOrWhiteSpace(answer) ? question! : $"Q: {question}\nA: {answer}";
        return new MemoryItem
        {
            UserId = userId,
            Type = MemoryType.TaskHistory,
            Key = Truncate(question!, 40),
            Content = Truncate(content, 1000),
            Importance = 0.5,
        };
    }

    private static string FormatMemoryBlock(IReadOnlyList<MemoryItem> items)
    {
        // 只注入非空、有意义的记忆项；按类型分组、限制条数避免指令膨胀。
        var lines = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Content))
            .Take(30)
            .Select(i => $"- [{i.Type}] {(string.IsNullOrWhiteSpace(i.Key) ? "" : i.Key + "：")}{i.Content.ReplaceLineEndings(" ")}");
        var text = string.Join("\n", lines);
        return string.IsNullOrEmpty(text) ? "" : "# 用户长期记忆（跨会话保留，供参考）\n" + text;
    }

    private static string? ExtractLastUserText(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null) return null;
        string? last = null;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User)
            {
                var t = TextOf(m);
                if (!string.IsNullOrWhiteSpace(t)) last = t;
            }
        }
        return last;
    }

    private static string ExtractAssistantText(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Assistant)
            {
                var t = TextOf(m);
                if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
            }
        }
        return sb.ToString().Trim();
    }

    private static string? TextOf(ChatMessage m)
    {
        var text = m.Text; // MEAI 聚合所有 TextContent
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
