using System.ComponentModel;
using System.Windows.Input;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>表单里单个问题的状态：选项 + 自定义文本 + 当前回答。</summary>
public partial class QuestionFormItem : ObservableObject
{
    public string Question { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private string? _customText;
    [ObservableProperty] private string? _selectedOption;

    public QuestionFormItem(string question, IReadOnlyList<string> options)
    {
        Question = question;
        Options = options ?? Array.Empty<string>();
    }

    /// <summary>该问的回答：选中选项优先，否则自定义文本，再否则空。</summary>
    public string Answer => !string.IsNullOrWhiteSpace(SelectedOption) ? SelectedOption
        : (!string.IsNullOrWhiteSpace(CustomText) ? CustomText! : "");

    [RelayCommand]
    private void SelectOption(string option) => SelectedOption = option;
}

/// <summary>提问/确认表单 ViewModel：多个问题一次提交，返回按序回答数组；取消返回 null。</summary>
public partial class AskUserDialogViewModel : ObservableObject
{
    public IReadOnlyList<QuestionFormItem> Questions { get; }

    public event Action? Finished;
    public string[]? Result { get; private set; }

    public AskUserDialogViewModel(IReadOnlyList<AskUserQuestionInput> questions)
    {
        Questions = questions.Select(q => new QuestionFormItem(q.Question, q.Options)).ToList();
    }

    [RelayCommand]
    private void Submit()
    {
        Result = Questions.Select(q => q.Answer).ToArray();
        Finished?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Finished?.Invoke();
    }
}
