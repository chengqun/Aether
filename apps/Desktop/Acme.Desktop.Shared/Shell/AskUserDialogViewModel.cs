using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>提问/确认对话框 ViewModel：展示问题 + 选项按钮 + 自由文本；返回用户回答。</summary>
public partial class AskUserDialogViewModel : ObservableObject
{
    public string Question { get; }
    public IReadOnlyList<string> Options { get; }

    /// <summary>用户回答；null=取消。命令触发后由 Service 读取并关窗。</summary>
    public string? Result { get; private set; }

    [ObservableProperty] private string? _customText;

    public event Action? Finished;

    public AskUserDialogViewModel(string question, IReadOnlyList<string> options)
    {
        Question = question;
        Options = options ?? Array.Empty<string>();
    }

    [RelayCommand]
    private void SelectOption(string option)
    {
        Result = option;
        Finished?.Invoke();
    }

    [RelayCommand]
    private void SubmitCustom()
    {
        if (!string.IsNullOrWhiteSpace(CustomText))
        {
            Result = CustomText;
            Finished?.Invoke();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Finished?.Invoke();
    }
}
