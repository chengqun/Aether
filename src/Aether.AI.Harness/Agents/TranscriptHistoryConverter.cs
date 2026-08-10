using Aether.AI.Core.Messages;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 把持久化的 transcript（<see cref="Message"/> 列表）还原为 LLM 的 <see cref="ChatMessage"/> 历史。
/// <para><b>关键规则</b>（违反→GLM/Anthropic 兼容端点 400）：</para>
/// <list type="bullet">
/// <item>跳过空 content 的 assistant 消息（reasoning 段 / plan 卡，content=""）——空 assistant 会触发 messages 非法。</item>
/// <item>Role=Tool 的工具步还原成 <b>配对</b>的两条：Assistant(FunctionCallContent) + User(FunctionResultContent)，
/// callId 匹配、相邻——避免孤儿 tool result。</item>
/// <item>末尾经 <see cref="Sanitize"/> 净化：合成空 CallId、丢弃孤儿 tool_use/tool_result、
/// 空 tool 返回占位、合并连续同 role、裁剪尾部非 assistant 消息（修「重启续聊 400」——
/// 上一轮若以 tool_result 收尾且无最终回答，新 user 消息会与尾部 user 连续→角色不交替→400）。</item>
/// </list>
/// </summary>
public static class TranscriptHistoryConverter
{
    public static List<ChatMessage> Convert(IEnumerable<Message> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var m in messages ?? Enumerable.Empty<Message>())
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    result.Add(new ChatMessage(ChatRole.User, m.Content ?? string.Empty));
                    break;

                case MessageRole.Assistant:
                    // 跳过 reasoning 段（kind=reasoning）和 plan 卡（kind=plan，content 空）——不进 LLM 历史。
                    if (MetaVal(m, "kind") is "reasoning" or "plan") break;
                    // 跳过空 content 的 assistant（防 400）。
                    if (string.IsNullOrWhiteSpace(m.Content)) break;
                    result.Add(new ChatMessage(ChatRole.Assistant, m.Content));
                    break;

                case MessageRole.Tool:
                    // 工具步还原成 FunctionCall(tool_use) + FunctionResult(tool_result) 配对。
                    // callId/toolName/output 缺省值由 Sanitize 兜底（合成非空 id / 占位返回）。
                    var callId = m.ToolCallId ?? string.Empty;
                    var toolName = string.IsNullOrWhiteSpace(m.ToolName) ? "tool" : m.ToolName!;
                    var input = MetaInputDict(m);
                    var output = MetaOutput(m) ?? "(无返回)";

                    var callMsg = new ChatMessage { Role = ChatRole.Assistant };
                    callMsg.Contents.Add(new FunctionCallContent(callId, toolName, input));
                    result.Add(callMsg);

                    var resultMsg = new ChatMessage { Role = ChatRole.User };
                    resultMsg.Contents.Add(new FunctionResultContent(callId, output));
                    result.Add(resultMsg);
                    break;
            }
        }
        return Sanitize(result);
    }

    /// <summary>
    /// 把 <see cref="ChatMessage"/> 历史净化为 GLM/Anthropic 兼容端点可接受的形状。修复 4 类 400 诱因：
    /// <para>1) 空 CallId → 合成非空 id（call↔result 按出现顺序配对，共享同一合成 id）；</para>
    /// <para>2) 孤儿 tool_use（无匹配 tool_result）/ 孤儿 tool_result（无匹配 tool_use）→ 丢弃；</para>
    /// <para>3) 空/null 工具返回 → 占位（避免 tool_result content 为空）；</para>
    /// <para>4) 角色不交替（连续同 role / 尾部 user）→ 合并连续同 role + 裁剪尾部非 assistant（含被孤立的 tool_use）。</para>
    /// <para>幂等：对已净化历史再跑一次无副作用。既用于 DB 恢复路径（<see cref="Convert"/>），
    /// 也用于内存累积路径（<see cref="RestoringChatHistoryProvider"/> 返回前）——后者修会话内孤儿 tool_use
    /// （审批挂起 / 出错时模型发了 tool_use 但无 tool_result 落地，下一轮会被当孤儿丢弃）。</para>
    /// </summary>
    public static List<ChatMessage> Sanitize(IEnumerable<ChatMessage>? messages)
    {
        var list = (messages ?? Enumerable.Empty<ChatMessage>()).Select(Clone).ToList();
        if (list.Count == 0) return list;

        SynthesizeEmptyCallIds(list);
        DropOrphanToolContents(list);
        FixEmptyToolResults(list);
        RemoveEmptyMessages(list);
        list = MergeConsecutiveSameRole(list);
        TrimTrailingNonAssistant(list);
        RemoveEmptyMessages(list);
        return list;
    }

    // 浅拷贝：Sanitize 会就地改 Contents（删/替换 content），不能改到调用方持有的原始消息。
    private static ChatMessage Clone(ChatMessage src)
    {
        var copy = new ChatMessage { Role = src.Role };
        foreach (var c in src.Contents) copy.Contents.Add(c);
        return copy;
    }

    /// <summary>空 CallId 合成：按出现顺序把第 N 个空 call 与第 N 个空 result 配对，赋同一合成 id。
    /// CallId 在 MEAI 中是 init-only，故重建 content 对象替换。多余的空 call/result 保持空 id，下一步当孤儿丢弃。</summary>
    private static void SynthesizeEmptyCallIds(List<ChatMessage> list)
    {
        var emptyCalls = new List<(ChatMessage msg, int idx, FunctionCallContent fc)>();
        var emptyResults = new List<(ChatMessage msg, int idx, FunctionResultContent fr)>();
        foreach (var msg in list)
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                switch (msg.Contents[i])
                {
                    case FunctionCallContent fc when string.IsNullOrEmpty(fc.CallId):
                        emptyCalls.Add((msg, i, fc)); break;
                    case FunctionResultContent fr when string.IsNullOrEmpty(fr.CallId):
                        emptyResults.Add((msg, i, fr)); break;
                }
            }
        int n = Math.Min(emptyCalls.Count, emptyResults.Count);
        for (int i = 0; i < n; i++)
        {
            var id = $"toolu_synth_{i}";
            var (cmsg, ci, fc) = emptyCalls[i];
            cmsg.Contents[ci] = new FunctionCallContent(id, fc.Name, fc.Arguments);
            var (rmsg, ri, fr) = emptyResults[i];
            rmsg.Contents[ri] = new FunctionResultContent(id, fr.Result);
        }
    }

    /// <summary>丢弃孤儿：无匹配 result 的 tool_use / 无匹配 call 的 tool_result（就地删 content）。</summary>
    private static void DropOrphanToolContents(List<ChatMessage> list)
    {
        var callIds = new HashSet<string>();
        var resultIds = new HashSet<string>();
        foreach (var msg in list)
            foreach (var c in msg.Contents)
            {
                if (c is FunctionCallContent fc) callIds.Add(fc.CallId ?? string.Empty);
                else if (c is FunctionResultContent fr) resultIds.Add(fr.CallId ?? string.Empty);
            }
        foreach (var msg in list)
            for (int i = msg.Contents.Count - 1; i >= 0; i--)
            {
                switch (msg.Contents[i])
                {
                    case FunctionCallContent fc when !resultIds.Contains(fc.CallId ?? string.Empty) || string.IsNullOrEmpty(fc.CallId):
                    case FunctionResultContent fr when !callIds.Contains(fr.CallId ?? string.Empty) || string.IsNullOrEmpty(fr.CallId):
                        msg.Contents.RemoveAt(i);
                        break;
                }
            }
    }

    /// <summary>空/null 工具返回 → 占位字符串（避免 tool_result content 为空被端点拒）。</summary>
    private static void FixEmptyToolResults(List<ChatMessage> list)
    {
        foreach (var msg in list)
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                if (msg.Contents[i] is FunctionResultContent fr && IsEmptyResult(fr.Result))
                    msg.Contents[i] = new FunctionResultContent(fr.CallId, "(无返回)");
            }
    }

    private static bool IsEmptyResult(object? result)
        => result is null || (result is string s && string.IsNullOrWhiteSpace(s));

    /// <summary>移除过滤后无 content 的消息。</summary>
    private static void RemoveEmptyMessages(List<ChatMessage> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Contents.Count == 0) list.RemoveAt(i);
    }

    /// <summary>合并连续同 role 消息（合并 contents）——解决持久化遗留的连续 user / 连续 assistant。</summary>
    private static List<ChatMessage> MergeConsecutiveSameRole(List<ChatMessage> list)
    {
        var merged = new List<ChatMessage>();
        foreach (var msg in list)
        {
            if (merged.Count > 0 && merged[^1].Role == msg.Role)
            {
                foreach (var c in msg.Contents) merged[^1].Contents.Add(c);
            }
            else
            {
                merged.Add(msg);
            }
        }
        return merged;
    }

    /// <summary>裁剪尾部非 assistant 消息：确保历史以 assistant 结尾（或为空），使框架追加的新 user
    /// 消息不会与尾部 user 连续→角色不交替。删尾部 user 后，其前导 assistant 中的 tool_use 沦为孤儿→
    /// 一并删（保留 text 旁白；若仅剩 tool_use 则删整条），反复直到尾部为 assistant(text) 或空。
    /// 代表「上一轮以 tool_result 收尾但无最终回答」的不完整轮次被整段丢弃——可接受（无答案可保）。</summary>
    private static void TrimTrailingNonAssistant(List<ChatMessage> list)
    {
        while (list.Count > 0)
        {
            var last = list[^1];
            // 尾部非 assistant（user/tool 等）→ 删（新 user 消息会接在 assistant 后才合法）
            if (last.Role != ChatRole.Assistant)
            {
                list.RemoveAt(list.Count - 1);
                continue;
            }
            // 尾部 assistant：删其中孤立的 tool_use（其 result 已随上一轮 user 删除）
            bool hasToolUse = last.Contents.Any(c => c is FunctionCallContent);
            if (!hasToolUse) break; // 纯 text assistant → 安全，停
            bool hasText = last.Contents.Any(c => c is TextContent);
            if (hasText)
            {
                for (int i = last.Contents.Count - 1; i >= 0; i--)
                    if (last.Contents[i] is FunctionCallContent) last.Contents.RemoveAt(i);
                break; // 只剩 text → 安全，停
            }
            // 仅 tool_use 无 text → 删整条，继续看新的尾部
            list.RemoveAt(list.Count - 1);
        }
    }

    private static string? MetaVal(Message m, string key)
        => m.Metadata is not null && m.Metadata.TryGetValue(key, out var v) ? v : null;

    private static IDictionary<string, object?>? MetaInputDict(Message m)
    {
        var json = MetaVal(m, "input");
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json); }
        catch { return null; }
    }

    private static object? MetaOutput(Message m)
    {
        var json = MetaVal(m, "output");
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json); }
        catch { return json; }
    }
}
