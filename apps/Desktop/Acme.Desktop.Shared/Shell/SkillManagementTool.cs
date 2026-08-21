using System.ComponentModel;
using Aether.AI.Harness.Skills;
using Aether.AI.Hosting;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>skill_list</c> / <c>skill_add</c> / <c>skill_remove</c> 工具：让 agent 管理文件式 skill。
/// skill 是目录扫描式能力（<c>&lt;SkillsDirectory&gt;/&lt;name&gt;/SKILL.md</c>，YAML frontmatter + Markdown 正文），
/// 运行时经 MAF <c>AgentFileSkillsSource</c> 直读目录，无需重启即可生效。与 <see cref="McpManagementTool"/>
/// 同属宿主层注册（<see cref="SkillRegistry"/> / <see cref="AetherAIOptions"/> 在平台层，非 ai/ 业务上下文）。
/// </summary>
/// <remarks>
/// 与 MCP 工具的关键区别：skill 不是「注册进 ToolRegistry 的工具」，而是「按需 <c>load_skill</c> 进上下文的
/// 提示词」。写 <c>SKILL.md</c> + <see cref="SkillRegistry.ReloadFromDirectory"/> 后，索引（name/desc）即反映到
/// 后续每轮上下文，agent 运行时 <c>load_skill</c> 直读盘取正文——能力注入的闸门在 add（不可逆安装知识），标
/// <c>Dangerous</c>；remove 同；list 只读标 Safe。
/// </remarks>
public sealed class SkillManagementTool
{
    private readonly SkillRegistry _registry;
    private readonly string? _skillsDirectory;

    public SkillManagementTool(SkillRegistry registry, AetherAIOptions options)
    {
        _registry = registry;
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
        var metas = _registry.GetAllMetas();
        return Task.FromResult(new ListOutput
        {
            Count = metas.Count,
            Directory = _skillsDirectory,
            Skills = metas.Select(m => new SkillItem { Name = m.Name, Description = m.Description, Category = m.Category }).ToList(),
        });
    }

    [Description("新增或更新一个 skill：写到 <skills>/<name>/SKILL.md（YAML frontmatter: name/description/可选 category + Markdown 正文）" +
                 "并立即重载索引，令其下轮起对 agent 生效（agent 运行时 load_skill 按需取正文）。" +
                 "skill 是流程知识/脚本能力——装一个 skill 等于给 agent 注入新流程知识，属不可轻率放行的写入口，需要用户审批确认。")]
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

        var dir = Path.Combine(_skillsDirectory, SafeDirName(name));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "SKILL.md"),
            BuildSkillMarkdown(name, description, category, content),
            cancellationToken).ConfigureAwait(false);

        _registry.ReloadFromDirectory(_skillsDirectory);
        return new MutateOutput { Success = true, Name = name };
    }

    [Description("删除一个 skill（按名删 <skills>/<name> 目录）并立即重载索引。此操作卸载该 skill，需要用户审批确认。")]
    public Task<MutateOutput> RemoveSkillAsync(
        [Description("要删除的 skill 名。")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_skillsDirectory))
            return Task.FromResult(new MutateOutput { Success = false, Error = "skill 目录未配置（SkillsDirectory 为空）。" });
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new MutateOutput { Success = false, Error = "name 不能为空。" });

        var dir = Path.Combine(_skillsDirectory, SafeDirName(name));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        _registry.ReloadFromDirectory(_skillsDirectory);
        return Task.FromResult(new MutateOutput { Success = true, Name = name });
    }

    /// <summary>生成与 <see cref="SkillRegistry.LoadFromDirectory"/> 期望一致的 SKILL.md（frontmatter + 正文）。</summary>
    private static string BuildSkillMarkdown(string name, string description, string? category, string content)
    {
        var cat = string.IsNullOrWhiteSpace(category) ? "通用" : category.Trim();
        return $"---\nname: {name}\ndescription: {description}\ncategory: {cat}\n---\n\n{content.TrimEnd('\n')}\n";
    }

    private static string SafeDirName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
        return sb.Length == 0 ? "skill" : sb.ToString();
    }
}
