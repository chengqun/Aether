namespace Acme.Notes.Domain.Agents;

/// <summary>Agent 记忆模式（与平台 Core 的 AgentMemoryMode 平行，business 不引 Aether.AI.*）。</summary>
public enum AgentMemoryMode
{
    SlidingWindow = 0,
    Full = 1,
    Off = 2,
}

/// <summary>Agent 响应格式（与平台 Core 的 AgentResponseFormat 平行，business 不引 Aether.AI.*）。</summary>
public enum AgentResponseFormat
{
    Default = 0,
    Text = 1,
    Json = 2,
}
