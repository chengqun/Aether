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
                    var callId = m.ToolCallId ?? string.Empty;
                    var toolName = m.ToolName ?? string.Empty;
                    var input = MetaInputDict(m);
                    var output = MetaOutput(m);

                    var callMsg = new ChatMessage { Role = ChatRole.Assistant };
                    callMsg.Contents.Add(new FunctionCallContent(callId, toolName, input));
                    result.Add(callMsg);

                    var resultMsg = new ChatMessage { Role = ChatRole.User };
                    resultMsg.Contents.Add(new FunctionResultContent(callId, output));
                    result.Add(resultMsg);
                    break;
            }
        }
        return result;
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
