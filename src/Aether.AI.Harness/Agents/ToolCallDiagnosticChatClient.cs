using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <b>临时诊断包装器</b>（调查「工具/ask_user 重复执行两遍」用，定位后移除）。
/// <para>
/// 包在真实 <see cref="IChatClient"/> 外层：FICC 每轮迭代调一次 <see cref="GetStreamingResponseAsync"/>，
/// 本包装在流结束后打印该轮模型响应里的工具调用——<b>每个 FunctionCallContent 的 CallId/名称/参数</b> + 文本预览。
/// 据此判定重复执行的根因：
/// <list type="bullet">
/// <item>单轮里出现 ≥2 个不同 CallId 的相同工具调用 → 机制(a)：模型在一个响应里发两个（或 OpenAI 流式适配器把一个拆成两个）。</item>
/// <item>两轮各 1 个、相同工具/参数 → 机制(b)：模型见到结果后下一轮重调。</item>
/// <item>同 CallId 出现两次 → 适配器拆分。</item>
/// </list>
/// 与 <see cref="ToolHistorySanitizingChatClient"/> 正交：后者记录<b>请求</b>(tools+msgs)，本类记录<b>响应</b>里的工具调用。
/// </para>
/// </summary>
public sealed class ToolCallDiagnosticChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _label;
    private int _seq;

    public ToolCallDiagnosticChatClient(IChatClient inner, string? label = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _label = label ?? "ToolDiag";
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 非流式路径（agent 走流式，此处一般不走；保留以防万一）
        var resp = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var byCallId = new Dictionary<string, (string Name, string Args)>();
        foreach (var m in resp.Messages)
            foreach (var c in m.Contents)
                Collect(c, byCallId);
        Log(Interlocked.Increment(ref _seq), byCallId, text: "");
        return resp;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        // 记录「请求」侧：GLM 这一轮到底看到了什么历史——尤其工具调用是否带着配对的结果。
        // 据此判定重复调用的根因：若 req#2 里 #1 的 call 仍带 ✓(有结果) → GLM 见到结果仍重调（quirk，去重守卫修）；
        // 若带 ✗(孤儿) → 结果没回喂给模型（历史 bug，修历史而非去重）。
        LogRequest(seq, messages);
        var stream = _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        return WithDiag(stream, seq);
    }

    private static void LogRequest(int seq, IEnumerable<ChatMessage> messages)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var calls = new List<(string Name, string Id)>();
        var resultIds = new HashSet<string>();
        foreach (var m in msgs)
            foreach (var c in m.Contents)
            {
                if (c is FunctionCallContent fc)
                    calls.Add((fc.Name ?? "?", fc.CallId ?? ""));
                else if (c is FunctionResultContent fr && !string.IsNullOrEmpty(fr.CallId))
                    resultIds.Add(fr.CallId);
            }
        var orphan = calls.Count(c => !resultIds.Contains(c.Id));
        var detail = string.Join(",", calls.Select(c =>
            $"{c.Name}[{(resultIds.Contains(c.Id) ? "ok" : "ORPHAN")}]"));
        var line = $"[req] #{seq}: {msgs.Count}msgs {calls.Count}calls {resultIds.Count}results {orphan}orphan | {detail}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> WithDiag(
        IAsyncEnumerable<ChatResponseUpdate> stream, int seq,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var byCallId = new Dictionary<string, (string Name, string Args)>();
        var text = new StringBuilder();
        await foreach (var u in stream.ConfigureAwait(false))
        {
            foreach (var c in u.Contents)
            {
                if (c is FunctionCallContent fc) Collect(fc, byCallId);
                else if (c is TextContent tc && !string.IsNullOrEmpty(tc.Text)) text.Append(tc.Text);
            }
            yield return u;
        }
        Log(seq, byCallId, text.ToString());
    }

    private static void Collect(AIContent c, Dictionary<string, (string Name, string Args)> byCallId)
    {
        if (c is not FunctionCallContent fc) return;
        // 同一 CallId 可能跨多个流式分片出现（增量参数）；按 CallId 归并，最后赢。
        var id = string.IsNullOrEmpty(fc.CallId) ? $"(noid){byCallId.Count}" : fc.CallId;
        var args = SerializeArgs(fc.Arguments);
        byCallId[id] = (fc.Name ?? "?", args);
    }

    private static string SerializeArgs(object? args)
    {
        if (args is null) return "";
        try { return JsonSerializer.Serialize(args); }
        catch { return args.ToString() ?? ""; }
    }

    private void Log(int seq, Dictionary<string, (string Name, string Args)> byCallId, string text)
    {
        var calls = string.Join("; ", byCallId.Select(p => $"{p.Value.Name}[{p.Key}] args={p.Value.Args}"));
        var preview = text.Length > 60 ? text[..60] + "…" : text;
        var line = $"[{_label}] LLM call #{seq} → {byCallId.Count} tool call(s): {calls}";
        if (text.Length > 0) line += $" | text=\"{preview}\"";
        // Console 确保桌面 `dotnet run` 终端可见；Debug 供 IDE 调试器；Trace 供移动端设备日志。
        Console.WriteLine(line);
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
