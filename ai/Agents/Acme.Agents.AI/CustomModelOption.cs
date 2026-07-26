namespace Acme.Agents.AI;

/// <summary>可选模型项（Id + 显示名），供专家编辑页下拉。</summary>
public sealed record CustomModelOption(string Id, string Name);

/// <summary>
/// 可选模型来源抽象，使 <c>ai/Agents</c> 与 VM 层都不依赖 <c>Acme.Llm.*</c>。
/// 宿主（桌面端）提供实现，把 LLM 配置映射成 <see cref="CustomModelOption"/>。
/// </summary>
public interface IModelSource
{
    /// <summary>返回可选模型列表（含"默认"项，Id 为空）。</summary>
    IReadOnlyList<CustomModelOption> GetModels();
}

/// <summary>默认模型源：仅返回一项"默认"（Id 空 = 用宿主默认模型）。</summary>
public sealed class DefaultModelSource : IModelSource
{
    public IReadOnlyList<CustomModelOption> GetModels()
        => new[] { new CustomModelOption(string.Empty, "默认（宿主配置）") };
}
