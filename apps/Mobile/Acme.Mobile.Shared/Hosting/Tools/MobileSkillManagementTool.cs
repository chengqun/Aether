using System.ComponentModel;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Skills;
using Aether.AI.Harness.Skills;
using Aether.AI.Hosting;

namespace Acme.Mobile.Shared.Hosting.Tools;

/// <summary>
/// 手机端 skill 管理工具（<c>skill_list</c>/<c>skill_add</c>/<c>skill_remove</c>）：
/// 镜像桌面 <c>SkillManagementTool</c>，但用 <see cref="ISkillSourceParser"/>（Notes Domain 契约，
/// 与 <c>SkillCreateViewModel</c> 同一序列化）写 <c>labs/skills/&lt;name&gt;/SKILL.md</c>，
/// 经 <see cref="SkillRegistry.ReloadFromDirectory"/> 重载索引立即生效。写盘即「创建」——
/// labs 是权威存储，云同步后桌面端可见。skill 是「按需 <c>load_skill</c> 进上下文的提示词能力」，
/// 装一个 skill = 注入新流程知识 → add/remove 标 Dangerous。
/// </summary>
public sealed class MobileSkillManagementTool
{
    private readonly SkillRegistry _registry;
    private readonly ISkillSourceParser _parser;
    private readonly string? _skillsDirectory;

    public MobileSkillManagementTool(SkillRegistry registry, ISkillSourceParser parser, AetherAIOptions options)
    {
        _registry = registry;
        _parser = parser;
        _skillsDirectory = options.SkillsDirectory;
    }

    public sealed class SkillItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }

    public sealed class ListOutput
    {
        public int Count { get; set; }
        public string? Directory { get; set; }
        public List<SkillItem> Skills { get; set; } = new();
    }

    public sealed class MutateOutput
    {
        public bool Success { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出当前已加载的 skill（名称/描述/分类）。只读，无副作用。添加/删除 skill 前先调此确认现有 skill，避免重名。")]
    public Task<ListOutput> ListSkillsAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_skillsDirectory))
        {
            try { _registry.ReloadFromDirectory(_skillsDirectory); }
            catch { /* 目录不存在/IO 异常——返回当前已加载索引，不阻断 */ }
        }
        var metas = _registry.GetAllMetas();
        return Task.FromResult(new ListOutput
        {
            Count = metas.Count,
            Directory = _skillsDirectory,
            Skills = metas.Select(m => new SkillItem { Name = m.Name, Description = m.Description, Category = m.Category }).ToList(),
        });
    }

    [Description("新增或更新一个 skill：写到 labs/skills/<name>/SKILL.md（YAML front-matter name/description/可选 category + markdown 正文）" +
                 "并立即重载索引，令其下轮起对 agent 生效（agent 运行时 load_skill 按需取正文）。装一个 skill 等于给 agent 注入新流程知识，需要用户审批确认。")]
    public async Task<MutateOutput> AddSkillAsync(
        [Description("skill 名（子目录名，建议英文短横线，如 my-workflow）。")] string name,
        [Description("skill 描述（一句话，进入每轮上下文索引供 agent 决策是否加载）。")] string description,
        [Description("skill 正文（Markdown，即加载后注入 agent 的指令）。可选含脚本说明。")] string content,
        [Description("可选分类标签，如「流程」「数据」。缺省「通用」。")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_skillsDirectory))
            return new MutateOutput { Success = false, Error = "skill 目录未配置（SkillsDirectory 为空）。" };
        if (string.IsNullOrWhiteSpace(name))
            return new MutateOutput { Success = false, Error = "name 不能为空。" };
        if (string.IsNullOrWhiteSpace(content))
            return new MutateOutput { Success = false, Error = "content 不能为空。" };

        var def = new SkillSourceDefinition
        {
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(category) ? "通用" : category.Trim(),
            Body = content,
        };
        var source = _parser.Serialize(def);

        var slug = Sanitize(name);
        var dir = Path.Combine(_skillsDirectory, slug);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), source, cancellationToken).ConfigureAwait(false);

        _registry.ReloadFromDirectory(_skillsDirectory);
        return new MutateOutput { Success = true, Name = name.Trim() };
    }

    [Description("删除一个 skill（按名删 labs/skills/<name> 目录）并立即重载索引。此操作卸载该 skill，需要用户审批确认。")]
    public Task<MutateOutput> RemoveSkillAsync(
        [Description("要删除的 skill 名。")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_skillsDirectory))
            return Task.FromResult(new MutateOutput { Success = false, Error = "skill 目录未配置（SkillsDirectory 为空）。" });
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new MutateOutput { Success = false, Error = "name 不能为空。" });

        var dir = Path.Combine(_skillsDirectory, Sanitize(name));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        _registry.ReloadFromDirectory(_skillsDirectory);
        return Task.FromResult(new MutateOutput { Success = true, Name = name.Trim() });
    }

    /// <summary>非法文件名字符替换为下划线（与 LabsFileWriter.Sanitize / DirectoryAgentCatalog.Sanitize 同语义）。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var trimmed = new string(chars).Trim();
        return string.IsNullOrEmpty(trimmed) ? "skill" : trimmed;
    }
}
