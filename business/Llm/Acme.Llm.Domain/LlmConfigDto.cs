namespace Acme.Llm.Domain;

/// <summary>
/// LLM config DTO — the shape exchanged with the app server and returned to
/// callers. Ported from AlphaAgent's <c>LlmConfigResponseDto</c>.
/// </summary>
public sealed class LlmConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ModelName { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public float Temperature { get; set; } = 0.5f;
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
}
