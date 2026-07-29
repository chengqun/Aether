namespace Aether.AI.Hosting;

/// <summary>
/// Top-level configuration for the Aether.AI platform.
/// </summary>
public sealed class AetherAIOptions
{
    /// <summary>OpenAI / Azure OpenAI connection settings used to build the chat client.</summary>
    public OpenAIOptions OpenAI { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, agents are built as lightweight <c>ChatClientAgent</c>s
    /// instead of the full <c>HarnessAgent</c> pipeline. Defaults to <c>false</c>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <summary>
    /// HarnessAgent「文件记忆」provider 的可写根目录（每 agent 在其下建子目录隔离）。
    /// 为 null 且某 agent 启用文件记忆时，平台会静默禁用该 provider（避免默认相对路径
    /// 在只读 CWD 下抛 Read-only file system）。桌面端应设为租户数据目录下的子目录。
    /// </summary>
    public string? FileMemoryRoot { get; set; }
}

/// <summary>
/// Connection settings for the OpenAI-compatible chat client.
/// </summary>
public sealed class OpenAIOptions
{
    /// <summary>API key. Falls back to the OPENAI_API_KEY environment variable when empty.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model / deployment id to use (e.g. "gpt-4o-mini").</summary>
    public string ModelId { get; set; } = "gpt-4o-mini";

    /// <summary>Optional Azure OpenAI endpoint. When set, an AzureOpenAIClient is used.</summary>
    public string? Endpoint { get; set; }
}
