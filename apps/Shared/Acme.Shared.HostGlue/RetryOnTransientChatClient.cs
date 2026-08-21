using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Acme.Shared.HostGlue;

/// <summary>
/// <see cref="IChatClient"/> 装饰器：对 GLM 等 OpenAI 兼容端点的**瞬态提前终止**异常自动重试一次。
/// <para>
/// 触发场景：上下文接近窗口边界时，GLM 偶发返回 <c>finish_reason=ResponseEnded</c>，MEAI 包成异常
/// "The response ended prematurely.(ResponseEnded)" 抛出。这类瞬态**重试通常能成**（非确定性塞爆），
/// 故本装饰器在流式零输出时透明重试一次，让偶发失败对用户无感。仅对消息含 "ended prematurely"/"prematurely"/"ResponseEnded"
/// 的异常重试——认证/格式/取消（<see cref="OperationCanceledException"/>）等不匹配，不重试。
/// </para>
/// <para>
/// 重试语义：流式 <b>仅当本轮零输出</b>（未 yield 任何 update）时重试，避免已吐 token 后重试导致内容重复；
/// 重试再抛则穿透上抛（不无限重试）。非流式整块重试（无部分输出问题）。
/// </para>
/// <para>
/// 切入点选在 chat client 装饰器层（<see cref="LlmConfigChatClientFactory.Wrap"/> 内层、紧贴 OpenAI client）：
/// 此层在 AIAgent 之下，<paramref name="messages"/> 是 AIAgent 调用时传入的只读引用，重试传同一 messages
/// 无 history 写入副作用（不会重复 append user message → 连续 user 角色 → 又报错）。覆盖所有经
/// <see cref="IChatClientFactory.Create"/> 的路径（agent 轮 + 摘要/标题/日报/记忆沉淀）。与
/// <see cref="Aether.AI.Harness.Agents.ToolHistorySanitizingChatClient"/>（手机外层装饰）同形：持 <c>_inner</c> 转发，
/// 无状态/线程安全（<c>_clients</c> GetOrAdd 缓存复用同一实例）。
/// </para>
/// <para>
/// 叠放：手机 <c>AIAgent → ToolHistorySanitizing → Retry → OpenAI</c>；桌面 <c>AIAgent → Retry → OpenAI</c>。
/// Retry 必须在 <see cref="Aether.AI.Harness.Agents.ToolHistorySanitizingChatClient"/> <b>内层</b>——瞬态异常在到达
/// Sanitizing 的异常包装（拼诊断信息重新抛出）前就被重试消化掉，避免"重试成功但已被包装成诊断异常"的错位。
/// </para>
/// </summary>
public sealed class RetryOnTransientChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public RetryOnTransientChatClient(IChatClient inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 非流式整块重试：无部分输出问题，瞬态即重试一次，重试再抛穿透。
        try
        {
            return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Debug.WriteLine($"[RetryOnTransientChatClient] 非流式瞬态异常，自动重试一次: {ex.GetType().Name}: {ex.Message}");
            return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => WithRetry(messages, options, cancellationToken);

    /// <summary>流式重试枚举：仅首次（attempt=0）零输出瞬态异常时重试一次，已吐 token 不重试（防重复）。
    /// 结构镜像 <see cref="Aether.AI.Harness.Agents.ToolHistorySanitizingChatClient.WithDiagnostics"/>：
    /// 外层 try 仅 finally（无 catch，故 yield 合法）；内层 try-with-catch 不含 yield（仅 break 触发重试）。</summary>
    private async IAsyncEnumerable<ChatResponseUpdate> WithRetry(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var stream = _inner.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
            bool emitted = false; // 本轮是否已吐 token（跨 attempt 重置）
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await stream.MoveNextAsync().ConfigureAwait(false); }
                    // 仅首次 + 零输出 + 瞬态 → break 跳出 while，finally Dispose 后外层 for 重试 attempt=1。
                    // attempt=1 时 when 守卫失败（attempt!=0）→ 异常穿透上抛，不无限重试。
                    catch (Exception ex) when (attempt == 0 && !emitted && IsTransient(ex))
                    {
                        Debug.WriteLine($"[RetryOnTransientChatClient] 流式瞬态异常（零输出），自动重试一次: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                    if (!next) yield break; // 正常完成
                    emitted = true;
                    yield return stream.Current;
                }
            }
            finally { await stream.DisposeAsync().ConfigureAwait(false); }
        }
        // 两次 attempt 均因瞬态 break 退出（attempt=1 不可能——其 catch 守卫 attempt==0 失败故穿透抛出），
        // 此 fall-through 仅占位，实际不可达；即使可达也仅以空流结束（安全降级）。
    }

    /// <summary>遍历异常 + InnerException 链，消息含明确 GLM 瞬态终止标记则重试。
    /// 保守匹配：认证/格式/取消等异常消息不含这些词，不重试。</summary>
    private static bool IsTransient(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message ?? string.Empty;
            if (msg.Contains("ended prematurely", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
