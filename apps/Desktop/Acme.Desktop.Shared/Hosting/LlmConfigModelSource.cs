using Acme.Agents.AI;
using Acme.Llm.Application;
using Acme.Llm.Domain;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// <see cref="IModelSource"/> 的桌面实现：把 <see cref="ILlmConfigService"/> 缓存的
/// LLM 配置映射为 <see cref="CustomModelOption"/>，供专家编辑页下拉。首项为"默认"（Id 空）。
/// </summary>
public sealed class LlmConfigModelSource : IModelSource
{
    private readonly ILlmConfigService _llmService;
    private IReadOnlyList<CustomModelOption>? _cache;

    public LlmConfigModelSource(ILlmConfigService llmService)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
    }

    public IReadOnlyList<CustomModelOption> GetModels()
    {
        if (_cache is not null) return _cache;

        List<LlmConfigDto>? configs = null;
        try { configs = Task.Run(() => _llmService.GetCachedAsync()).GetAwaiter().GetResult(); }
        catch { /* 未登录/未同步时退化为仅默认 */ }

        var list = new List<CustomModelOption> { new(string.Empty, "默认（宿主配置）") };
        if (configs is not null)
        {
            foreach (var c in configs)
                list.Add(new CustomModelOption(c.Id.ToString(), $"{c.Name}（{c.ModelName}）"));
        }

        _cache = list;
        return list;
    }
}
