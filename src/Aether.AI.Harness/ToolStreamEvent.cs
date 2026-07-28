using Microsoft.Extensions.AI;

namespace Aether.AI.Harness;

/// <summary>流式运行期间的一次工具事件（调用 / 结果），供 UI 展示思考过程中的工具使用。</summary>
public enum ToolStreamEventKind
{
    /// <summary>模型发起工具调用（FunctionCallContent）。</summary>
    Call,

    /// <summary>工具返回结果（FunctionResultContent）。</summary>
    Result,
}

/// <summary>
/// 流式运行期间工具调用/结果的结构化事件。Input/Output 为可空字典，便于 UI 直接序列化展示。
/// </summary>
public sealed record ToolStreamEvent(
    ToolStreamEventKind Kind,
    string Name,
    string CallId,
    Dictionary<string, object>? Input,
    Dictionary<string, object>? Output);
