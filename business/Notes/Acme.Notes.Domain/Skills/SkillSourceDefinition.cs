namespace Acme.Notes.Domain.Skills;

/// <summary>
/// 技能定义（最简模型）：name + description + category + markdown 正文。
/// 与知识对象同构（front-matter + 正文），但无 typed links 图边——技能是单点提示词能力，
/// 被 agent 按需 <c>load_skill</c> 引用。name = 技能文件夹名（slug，不在内容里），
/// 同时也是 front-matter <c>name:</c> 字段（运行时 SkillRegistry 用 front-matter name，回退到文件夹名）。
/// </summary>
public sealed class SkillSourceDefinition
{
    /// <summary>技能显示名（front-matter name）。与文件夹名(slug)通常一致但可不同。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>一句话描述（供 agent 索引判断是否 load）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>分类（自由字符串，如 通用/分析/工具）。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>markdown 正文（技能指令 / 脚本调用说明 / 任何 markdown）。agent load_skill 后读此正文。</summary>
    public string Body { get; set; } = string.Empty;
}
