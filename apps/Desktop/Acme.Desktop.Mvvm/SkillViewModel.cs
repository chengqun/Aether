using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Aether.AI.Harness.Skills;
using Aether.AI.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 技能管理 ViewModel（专家页「技能」tab，只读展示）：从 <see cref="SkillRegistry"/> 列全部已加载 skill
/// （name/category/description + 正文预览）。skill 是文件式（&lt;tenantDir&gt;/skills/&lt;name&gt;/SKILL.md，
/// drop-to-use）——此 tab 不内置编辑器（编辑用文档页/外部编辑器，文档页已能编辑 .md），仅提供「打开 skills 目录」入口
/// （目录路径来自 <see cref="AetherAIOptions.SkillsDirectory"/>）。照 <see cref="McpViewModel"/> 范式：
/// 注入服务 + IUiDispatcher，<see cref="LoadAsync"/> 映射成卡片。
/// </summary>
public partial class SkillViewModel : ObservableObject
{
    private readonly SkillRegistry _registry;
    private readonly IUiDispatcher _dispatcher;

    public ObservableCollection<SkillItem> Skills { get; } = new();

    /// <summary>skill 目录路径（&lt;tenantDir&gt;/skills），供 View「打开目录」按钮跳文件管理器。null=未配目录（按钮隐藏）。</summary>
    public string? SkillsDirectory { get; }

    /// <summary>是否配了 skills 目录（驱动「打开 skills 目录」按钮可见性）。</summary>
    public bool HasSkillsDirectory => !string.IsNullOrEmpty(SkillsDirectory);

    [ObservableProperty] private bool _isLoading;

    public SkillViewModel(SkillRegistry registry, AetherAIOptions options, IUiDispatcher dispatcher)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        SkillsDirectory = options?.SkillsDirectory;
    }

    /// <summary>重扫 skills 目录加载全部 skill 映射成卡片。照 <see cref="McpViewModel.LoadAsync"/> 范式：
    /// 后台线程重扫盘 + dispatcher 上 clear+add。重扫让 TAB 反映磁盘实时态（新增/编辑/删除 SKILL.md
    /// 后切 tab 或点刷新即出现，无需重启）——与运行时 <c>AgentFileSkillsSource</c> 直读目录的行为对齐。</summary>
    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            var items = await Task.Run(() =>
                {
                    // 重扫盘：registry 启动期仅被目录填充（无程序式 contributor），ReloadFromDirectory
                    // 清空+重扫安全。磁盘 I/O 在此后台线程，不阻塞 UI。
                    if (!string.IsNullOrEmpty(SkillsDirectory))
                        _registry.ReloadFromDirectory(SkillsDirectory);
                    return _registry.GetAll()
                        .Select(s => new SkillItem
                        {
                            Name = s.Name,
                            Category = string.IsNullOrWhiteSpace(s.Category) ? "通用" : s.Category,
                            Description = s.Description ?? string.Empty,
                            Content = s.Content ?? string.Empty,
                            Preview = BuildPreview(s.Description, s.Content),
                        })
                        .ToList();
                }).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                Skills.Clear();
                foreach (var i in items) Skills.Add(i);
            });
        }
        catch { /* 加载失败不阻断 */ }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    /// <summary>卡片预览：有 description 用 description；否则截 Content 前 140 字（无 description 时正文是唯一信息）。</summary>
    private static string BuildPreview(string? description, string? content)
    {
        if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        if (string.IsNullOrEmpty(content)) return "（无描述）";
        var t = content.Trim();
        const int Max = 140;
        return t.Length > Max ? t[..Max] + "…" : t;
    }
}
