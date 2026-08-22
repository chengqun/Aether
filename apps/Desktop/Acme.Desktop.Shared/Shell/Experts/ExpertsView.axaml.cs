using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class ExpertsView : UserControl
{
    public ExpertsView()
    {
        InitializeComponent();
    }

    private ExpertsViewModel? VM => DataContext as ExpertsViewModel;

    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    private async void OnAddExpertClick(object sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        try
        {
            await VM.Mcp.EnsureLoadedAsync(); // 确保 MCP 连完 + 刷新，编辑器 MCP 区用最新工具
            var vm = VM.CreateEditViewModel(null);
            await ShowDialogAsync(vm);
        }
        catch (Exception ex)
        {
            VM.StatusMessage = $"打开编辑窗口失败：{ex.Message}";
        }
    }

    private void OnToggleSummonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
            VM.ToggleSummonCommand.Execute(item);
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
        {
            try
            {
                await VM.Mcp.EnsureLoadedAsync(); // 确保 MCP 连完 + 刷新，编辑器 MCP 区用最新工具
                var vm = VM.CreateEditViewModel(item);
                await ShowDialogAsync(vm);
            }
            catch (Exception ex)
            {
                VM.StatusMessage = $"打开编辑窗口失败：{ex.Message}";
            }
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
            VM.DeleteExpertCommand.Execute(item);
    }

    private async System.Threading.Tasks.Task ShowDialogAsync(ExpertEditViewModel vm)
    {
        if (VM == null) return;
        try
        {
            var window = new ExpertEditWindow { DataContext = vm };
            var owner = GetOwnerWindow();

            // 自动保存：保存到 DB + invalidate，不关窗
            vm.AutoSaveRequested += async saved =>
            {
                await VM.SaveAsync(saved);
            };
            // 手动保存（点"完成"）：保存 + 关窗
            vm.SaveRequested += async saved =>
            {
                await VM.SaveAsync(saved);
                window.Close();
            };
            vm.CancelRequested += () => window.Close();

            if (owner != null)
                await window.ShowDialog(owner);
            else
                window.Show();
        }
        catch (Exception ex)
        {
            VM.StatusMessage = $"显示窗口失败：{ex.Message}";
        }
    }
}
