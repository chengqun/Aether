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
