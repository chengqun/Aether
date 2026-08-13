using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatClient"/> 包装器：在每次发请求前对 messages 跑 <see cref="TranscriptHistoryConverter.SanitizeLive"/>，
/// 修掉孤儿 tool 消息（tool_result 无前置 tool_call / tool_call 无后续 tool_result）、空 CallId、空 tool 返回——
/// 防 DeepSeek 等 OpenAI 兼容端点报「Messages with role tool must be a response to a preceding message with tool_calls」400。
/// <para>与 <see cref="ToolFilteringChatClient"/> 同形：持 <c>_inner</c> 转发。活路径用 <see cref="TranscriptHistoryConverter.SanitizeLive"/>
/// （非 <see cref="TranscriptHistoryConverter.Sanitize"/>）——续轮请求合法地以 tool_result 收尾，不能裁尾部非 assistant。</para>
/// <para>配对完整的合法历史原样放行（幂等无副作用）。仅当结构被修正时打 Debug 日志，便于定位孤儿来源
/// （NoMemory 跨步重置 / 滑窗裁断 / 审批挂起残留等）。</para>
/// </summary>
public sealed class ToolHistorySanitizingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _label;

    public ToolHistorySanitizingChatClient(IChatClient inner, string? label = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _label = label ?? nameof(ToolHistorySanitizingChatClient);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sanitized = TranscriptHistoryConverter.SanitizeLive(messages);
        LogIfChanged(messages, sanitized);
        return _inner.GetResponseAsync(sanitized, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sanitized = TranscriptHistoryConverter.SanitizeLive(messages);
        LogIfChanged(messages, sanitized);
        return _inner.GetStreamingResponseAsync(sanitized, options, cancellationToken);
    }

    private void LogIfChanged(IEnumerable<ChatMessage> original, List<ChatMessage> sanitized)
    {
        // 始终打一行结构摘要（便于定位 400 时的真实消息形状）；被修正时额外标注 SANITIZED。
        var origList = original as List<ChatMessage> ?? original.ToList();
        var changed = origList.Count != sanitized.Count || OrigChanged(origList, sanitized);
        Debug.WriteLine($"[{_label}] {(changed ? "SANITIZED" : "ok")} before={Summarize(origList)} after={Summarize(sanitized)}");
    }

    private static bool OrigChanged(List<ChatMessage> orig, List<ChatMessage> san)
    {
        if (orig.Count != san.Count) return true;
        for (int i = 0; i < orig.Count; i++)
        {
            if (orig[i].Role != san[i].Role || orig[i].Contents.Count != san[i].Contents.Count)
                return true;
            for (int j = 0; j < orig[i].Contents.Count; j++)
                if (orig[i].Contents[j].GetType() != san[i].Contents[j].GetType())
                    return true;
        }
        return false;
    }

    /// <summary>紧凑摘要：每条消息「角色:内容类型[CallId]」，用 | 连接——揭示顺序与配对。</summary>
    private static string Summarize(IList<ChatMessage> messages)
    {
        if (messages.Count == 0) return "[]";
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            var m = messages[i];
            sb.Append(m.Role.Value switch
            {
                "user" => "U", "assistant" => "A", "system" => "S", "tool" => "T", _ => "?",
            });
            sb.Append(':');
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent:
                        sb.Append('t'); break;
                    case FunctionCallContent fc:
                        sb.Append("c[").Append(string.IsNullOrEmpty(fc.CallId) ? "_" : fc.CallId).Append(']'); break;
                    case FunctionResultContent fr:
                        sb.Append("r[").Append(string.IsNullOrEmpty(fr.CallId) ? "_" : fr.CallId).Append(']'); break;
                    default:
                        sb.Append(c.GetType().Name[0]); break;
                }
            }
        }
        return sb.ToString();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
