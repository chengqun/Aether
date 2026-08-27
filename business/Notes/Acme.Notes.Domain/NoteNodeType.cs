namespace Acme.Notes.Domain;

public enum NoteNodeType
{
    Note = 0,
    Agent = 1,
    Workflow = 2,
    Knowledge = 3,
    Rule = 4,
    CodeRule = 5,
    CodeStrategy = 6,
    Strategy = 7,
    /// <summary>Lean 算法策略：直接写 QCAlgorithm 子类（复用 Lean 全部能力），与 Lean 耦合。全宽代码编辑器。</summary>
    LeanAlgorithm = 8,
    /// <summary>技能：文件夹节点 <c>skills/&lt;name&gt;/SKILL.md</c>（YAML front-matter name/description/category + markdown 正文）+ 可选兄弟脚本。
    /// 镜像知识对象的两栏 form（无 links）。技能是跨 agent 按需 <c>load_skill</c> 引用的提示词能力，非 chat 目标、无运行时缓存。</summary>
    Skill = 9,
}
