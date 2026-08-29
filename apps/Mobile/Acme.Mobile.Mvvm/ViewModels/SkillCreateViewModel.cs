using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 技能新建 VM（AI 设置 → 新建 Skill）。字段 = <see cref="SkillSourceDefinition"/>（name/description/category/body），
/// 经 <see cref="ISkillSourceParser.Serialize"/> 生成 SKILL.md 源文本，写 <c>labs/skills/&lt;name&gt;/SKILL.md</c>。
/// </summary>
public partial class SkillCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private readonly ISkillSourceParser _parser;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public SkillCreateViewModel(Func<string?> labsRoot, ISkillSourceParser parser)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
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
            await Task.Run(() => LabsFileWriter.WriteAsync(
                _labsRoot() ?? string.Empty, Acme.Notes.Domain.NoteNodeType.Skill, def.Name, source));
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

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
