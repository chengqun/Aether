using System.ComponentModel;
using System.Windows.Input;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>表单里单个问题的状态：选项 + 自定义文本 + 当前回答。</summary>
public partial class QuestionFormItem : ObservableObject
{
    public int Index { get; }
    public string Question { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private string? _customText;
    [ObservableProperty] private string? _selectedOption;

    /// <summary>Tab 标签：序号 + 问题前几个字。</summary>
    public string TabLabel => $"{Index + 1}. {(Question.Length > 8 ? Question[..8] + "…" : Question)}";

    /// <summary>是否已回答（选中选项或填了自定义文本）。</summary>
    public bool IsAnswered => !string.IsNullOrWhiteSpace(SelectedOption) || !string.IsNullOrWhiteSpace(CustomText);

    public string Answer => !string.IsNullOrWhiteSpace(SelectedOption) ? SelectedOption
        : (!string.IsNullOrWhiteSpace(CustomText) ? CustomText! : "");

    public QuestionFormItem(int index, string question, IReadOnlyList<string> options)
    {
        Index = index;
        Question = question;
        Options = options ?? Array.Empty<string>();
    }

    partial void OnSelectedOptionChanged(string? value) => OnPropertyChanged(nameof(IsAnswered));
    partial void OnCustomTextChanged(string? value) => OnPropertyChanged(nameof(IsAnswered));
}

/// <summary>提问/确认表单 ViewModel：多个问题一次提交，返回按序回答数组；取消返回 null。</summary>
public partial class AskUserDialogViewModel : ObservableObject
{
    public IReadOnlyList<QuestionFormItem> Questions { get; }

    public event Action? Finished;
    public string[]? Result { get; private set; }

    /// <summary>当前选中的 tab 索引。</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>已回答的问题数（提交按钮提示用）。</summary>
    public int AnsweredCount => Questions.Count(q => q.IsAnswered);

    public AskUserDialogViewModel(IReadOnlyList<AskUserQuestionInput> questions)
    {
        Questions = questions.Select((q, i) => new QuestionFormItem(i, q.Question, q.Options)).ToList();
        foreach (var q in Questions)
        {
            q.PropertyChanged += (_, _) => OnPropertyChanged(nameof(AnsweredCount));
            // 选中选项后自动跳到下一个未回答的 tab
            q.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuestionFormItem.SelectedOption) && q.IsAnswered)
                    AdvanceToNextUnanswered(q.Index);
            };
        }
    }

    /// <summary>跳到下一个未回答的问题（跳过已回答的）。最后一个则不跳。</summary>
    private void AdvanceToNextUnanswered(int currentIndex)
    {
        for (int i = currentIndex + 1; i < Questions.Count; i++)
        {
            if (!Questions[i].IsAnswered)
            {
                SelectedTabIndex = i;
                return;
            }
        }
        // 后面都回答了 → 往前找未回答的
        for (int i = 0; i < currentIndex; i++)
        {
            if (!Questions[i].IsAnswered)
            {
                SelectedTabIndex = i;
                return;
            }
        }
        // 全回答了 → 停在当前
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
