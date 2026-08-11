namespace Acme.Notes.Domain.Agents;

/// <summary>Agent 记忆模式（与平台 Core 的 AgentMemoryMode 平行，business 不引 Aether.AI.*）。</summary>
public enum AgentMemoryMode
{
    SlidingWindow = 0,
    Full = 1,
    Off = 2,
    /// <summary>摘要压缩：保留最近 N 条，更早的由 LLM 自动摘要（与平台 Core Summarize 平行，按名 round-trip）。</summary>
    Summarize = 3,
}

/// <summary>Agent 响应格式（与平台 Core 的 AgentResponseFormat 平行，business 不引 Aether.AI.*）。</summary>
public enum AgentResponseFormat
{
    Default = 0,
    Text = 1,
    Json = 2,
}
