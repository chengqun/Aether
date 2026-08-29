using System;
using System.IO;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 技能新建/编辑 VM（AI 设置 → 技能）。字段 = <see cref="SkillSourceDefinition"/>（name/description/category/body），
/// 经 <see cref="ISkillSourceParser.Serialize"/> 生成 SKILL.md 源文本，写 <c>labs/skills/&lt;name&gt;/SKILL.md</c>。
/// 编辑态经导航参数 <c>name</c>（技能名，= 文件夹名 slug）加载已有 SKILL.md 回填，保存覆盖同名目录，可删除。
/// </summary>
public partial class SkillCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private readonly ISkillSourceParser _parser;

    /// <summary>编辑态：原始技能名（slug，= 文件夹名）。null=新建。</summary>
    private string? _editingName;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>是否编辑态（true=编辑已有技能，显示删除按钮；false=新建）。</summary>
    public bool IsEditing => _editingName != null;

    /// <summary>页标题（编辑/新建）。</summary>
    public string ScreenTitle => IsEditing ? "编辑技能" : "新建技能";

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public SkillCreateViewModel(Func<string?> labsRoot, ISkillSourceParser parser)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>接收导航参数：<c>name</c> 非空 = 编辑态（加载已有技能回填）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var raw) && raw is string name && !string.IsNullOrWhiteSpace(name))
        {
            _editingName = name;
            LoadExisting();
        }
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ScreenTitle));
    }

    private void LoadExisting()
    {
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root) || _editingName == null) return;
        var skillDir = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Skill).DirectoryName, LabsFileWriter.Sanitize(_editingName));
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        if (!File.Exists(skillFile)) return;
        try
        {
            var text = File.ReadAllText(skillFile);
            if (_parser.TryParse(text, out var def, out _) && def != null)
            {
                Name = def.Name;
                Description = def.Description;
                Category = def.Category;
                Body = def.Body;
            }
        }
        catch { /* 读/解析失败 → 保留空白表单 */ }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "请填写技能名称";
            return;
        }
        IsLoading = true;
        try
        {
            var def = new SkillSourceDefinition
            {
                Name = Name.Trim(),
                Description = Description?.Trim() ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(Category) ? "通用" : Category.Trim(),
                Body = Body ?? string.Empty,
            };
            var source = _parser.Serialize(def);
            var checkError = await Task.Run<string?>(() =>
            {
                // 编辑态改名：先删旧目录，再写新目录（否则残留旧 slug 目录）。
                if (_editingName != null
                    && !string.Equals(LabsFileWriter.Sanitize(_editingName), LabsFileWriter.Sanitize(def.Name), StringComparison.Ordinal))
                {
                    var root = _labsRoot();
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var oldDir = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Skill).DirectoryName, LabsFileWriter.Sanitize(_editingName));
                        try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true); }
                        catch (Exception ex) { return $"删除旧技能失败: {ex.Message}"; }
                    }
                }
                LabsFileWriter.WriteAsync(_labsRoot() ?? string.Empty, NoteNodeType.Skill, def.Name, source).GetAwaiter().GetResult();
                return null;
            });
            if (checkError != null) { StatusMessage = checkError; return; }
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除（仅编辑态；删整个 skills/<name> 目录）。</summary>
    [RelayCommand]
    private void Delete()
    {
        if (_editingName == null) return;
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var dir = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Skill).DirectoryName, LabsFileWriter.Sanitize(_editingName));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            StatusMessage = "已删除";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
            return;
        }
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
    }

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
