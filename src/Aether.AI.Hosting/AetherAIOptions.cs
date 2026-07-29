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

    /// <summary>
    /// skills 目录（每个 skill 占一个子目录：&lt;dir&gt;/&lt;name&gt;/SKILL.md，可含脚本/资源）。
    /// 配置后 AgentFactory 用 MAF 的 AgentFileSkillsSource 加载（支持 SKILL.md 正文 + 可执行脚本）。
    /// 为 null 时退回 SkillRegistry 的纯提示词 source（不支持脚本）。
    /// </summary>
    public string? SkillsDirectory { get; set; }

    /// <summary>
    /// 执行 .py skill 脚本的解释器（默认 "python3"）。从 .app 启动时 GUI 进程 PATH 受限，
    /// 建议填绝对路径（如 /usr/bin/python3 / /opt/homebrew/bin/python3）。
    /// </summary>
    public string? PythonExecutable { get; set; }
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
