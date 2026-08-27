using Acme.Notes.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 技能（<c>SKILL.md</c> 文件）表单 VM（UI-agnostic，零 Avalonia）。
/// 镜像 <see cref="DocumentKnowledgeViewModel"/> 的 front-matter + body + 双向同步，<b>去掉 links/图</b>——
/// 技能是单点提示词能力，无 typed 图边。表单字段 = front-matter name/description/category + 只读 body 预览。
/// <para>技能身份 = 文件夹名 slug（<see cref="FolderName"/>，get-only；重命名走文件树）。
/// front-matter <c>name:</c> 是显示名（agent 索引用），可与 slug 不同——运行时 SkillRegistry 用 front-matter name、回退到文件夹名。</para>
/// <para>双向同步：表单字段变→<see cref="ScheduleSerialize"/>→<see cref="SourceChanged"/>（生成 YAML 源码）→
/// 宿主 KnowledgeObjectsView 回写编辑器；源码编辑→<see cref="SyncFromSource"/>→重解析回填表单。
/// <c>_loading</c>/<c>_syncing</c> 双守卫防回环。保存走 labs 页 <c>SaveAsync</c>（同知识对象路径）。</para>
/// </summary>
public sealed partial class SkillEditViewModel : ObservableObject
{
    private readonly ISkillSourceParser _parser;
    private bool _loading = true; // 构造期 + Load 期守卫，防初始填充触发 SourceChanged
    private bool _syncing;        // 源码→表单同步期守卫，防回环

    public SkillEditViewModel(ISkillSourceParser parser, string name)
    {
        _parser = parser;
        FolderName = name ?? string.Empty;
    }

    /// <summary>技能文件夹名（slug，身份；get-only）。重命名走文件树右键，不在表单编辑。</summary>
    public string FolderName { get; }

    /// <summary>front-matter name（显示名；供 agent 索引判断是否 load）。空时运行时回退到 <see cref="FolderName"/>。</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>一句话描述（供 agent 索引判断是否 load_skill）。</summary>
    [ObservableProperty] private string _description = string.Empty;

    /// <summary>分类（自由字符串，如 通用/分析/工具）。</summary>
    [ObservableProperty] private string _category = string.Empty;

    /// <summary>markdown 正文只读预览（源码驱动，下栏编辑→SyncFromSource→Body 刷新；不接 serialize）。</summary>
    [ObservableProperty] private string _body = string.Empty;

    /// <summary>配置折叠时的摘要文本（「name: xxx · 分类」），供 Expander.Header 显示。</summary>
    public string ConfigSummary =>
        $"name: {(string.IsNullOrEmpty(Name) ? FolderName : Name)} · {(string.IsNullOrEmpty(Category) ? "（未分类）" : Category)}";

    /// <summary>表单→源码：字段变更后重新序列化的 YAML 源文本。宿主订阅回写编辑器。</summary>
    public event Action<string>? SourceChanged;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(ConfigSummary));
        ScheduleSerialize();
    }

    partial void OnCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(ConfigSummary));
        ScheduleSerialize();
    }

    partial void OnDescriptionChanged(string value) => ScheduleSerialize();

    // Body 是只读预览（源码驱动），不接 serialize——OnBodyChanged 故意不定义。

    /// <summary>从源文本载入（初始 / 切到该文件）。_loading 守卫不发 SourceChanged。</summary>
    public void Load(string source)
    {
        _loading = true;
        try
        {
            if (_parser.TryParse(source, out var def, out _) && def is not null)
            {
                Name = def.Name ?? string.Empty;
                Description = def.Description ?? string.Empty;
                Category = def.Category ?? string.Empty;
                Body = def.Body ?? string.Empty;
            }
            else
            {
                // 解析失败：表单留空不崩，源码仍可编辑（下栏）
                Name = string.Empty;
                Description = string.Empty;
                Category = string.Empty;
                Body = string.Empty;
            }
        }
        finally { _loading = false; }
    }

    /// <summary>源码→表单（用户在下栏编辑源码后）。_syncing 守卫不发 SourceChanged（防回环）。</summary>
    public void SyncFromSource(string source)
    {
        _syncing = true;
        try { Load(source); } // Load 置 _loading=true；_syncing 叠加使 OnXxxChanged 跳过 serialize
        finally { _syncing = false; }
        OnPropertyChanged(nameof(ConfigSummary)); // Load 期守卫跳过了字段变更通知，此处补摘要刷新
    }

    private void ScheduleSerialize()
    {
        if (_loading || _syncing) return;
        SourceChanged?.Invoke(_parser.Serialize(Build()));
    }

    /// <summary>把可编辑字段投影回 Domain 模型（front-matter name/description/category + body）。</summary>
    public SkillSourceDefinition Build() => new()
    {
        Name = Name ?? string.Empty,
        Description = Description ?? string.Empty,
        Category = Category ?? string.Empty,
        Body = Body ?? string.Empty,
    };
}
