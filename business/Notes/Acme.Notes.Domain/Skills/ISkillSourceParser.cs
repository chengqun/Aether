namespace Acme.Notes.Domain.Skills;

/// <summary>
/// 技能源码解析器：SKILL.md = YAML front-matter（name/description/category）+ markdown 正文。
/// 镜像 <see cref="Acme.Notes.Domain.Knowledge.IDocumentKnowledgeSourceParser"/> 的 front-matter 拆分逻辑，
/// 去掉 links——技能无图边。容错无 front-matter 的旧文件（全文当正文）。
/// 序列化往返稳定：保持用户既有 SKILL.md 格式（front-matter + 正文）。
/// </summary>
public interface ISkillSourceParser
{
    bool TryParse(string source, out SkillSourceDefinition? definition, out string error);
    string Serialize(SkillSourceDefinition definition);
}
