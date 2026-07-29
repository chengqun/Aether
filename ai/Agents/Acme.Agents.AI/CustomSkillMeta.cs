namespace Acme.Agents.AI;

/// <summary>
/// UI 无关的 skill 元数据快照（名称 + 描述 + 分类），供专家编辑页勾选展示。
/// 从 <c>Aether.AI.Harness.Skills.SkillMeta</c> 映射而来，使 VM 层不必引用 Harness。
/// </summary>
public sealed record CustomSkillMeta(string Name, string Description, string Category);
