using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;

namespace Acme.Shared.HostGlue;

/// <summary>
/// 启动期 LLM 配置引导（UI 无关，桌面/手机共用）。读 SQLite 活跃配置；无则从
/// OPENAI_API_KEY/MODEL/ENDPOINT 种子一条默认配置（默认 DeepSeek）。
/// 合并自原 <c>DesktopLlmBootstrap</c> 与 <c>MobileLlmBootstrap</c>——唯一差异是 seed Name，
/// 故参数化为 <paramref name="defaultName"/>（桌面 "desktop-default" / 手机 "mobile-default"）。
/// </summary>
public static class LlmBootstrap
{
    public static LlmConfig GetOrCreateActive(string dbPath, string defaultName)
    {
        var repo = SqliteLlmConfigRepository.ForFile(dbPath);
        var all = repo.GetAllAsync().GetAwaiter().GetResult();
        var active = all.FirstOrDefault(c => c.IsDefault) ?? all.FirstOrDefault();
        if (active != null) return active;

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        active = new LlmConfig
        {
            Id = Guid.NewGuid(),
            Name = defaultName,
            ModelName = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "deepseek-chat",
            ApiKey = string.IsNullOrWhiteSpace(envKey) ? "dummy" : envKey,
            Endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "https://api.deepseek.com",
            Temperature = 0.5f,
            IsDefault = true,
        };
        repo.UpsertRangeAsync(new[] { active }).GetAwaiter().GetResult();
        return active;
    }
}
