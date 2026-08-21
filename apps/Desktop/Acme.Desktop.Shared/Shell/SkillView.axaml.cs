using System.Diagnostics;
using System.IO;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>技能 tab 视图（专家页）：只读卡片列表 + 正文 Markdown 预览 + 「打开 skills 目录」入口。
/// skill 是文件式（drop SKILL.md 即用），不内置编辑器——编辑用文档页/外部编辑器。照 <see cref="McpView"/> 范式。</summary>
public partial class SkillView : UserControl
{
    public SkillView() => InitializeComponent();

    /// <summary>打开 skills 目录（文件管理器）：编辑 SKILL.md 用文档页/外部编辑器。
    /// UseShellExecute=true 让系统用默认程序打开目录（mac Finder / Windows Explorer）。目录不存在/未配则静默返回。</summary>
    public void OnOpenSkillsFolder(object? sender, RoutedEventArgs e)
    {
        var dir = (DataContext as SkillViewModel)?.SkillsDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { /* 打开失败静默，不影响主流程 */ }
    }

    /// <summary>切换卡片正文预览展开/收起（就地交互，照专家卡 OnToggleSummonClick 范式——Tag 带 DataContext）。</summary>
    public void OnToggleContent(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SkillItem item)
            item.IsExpanded = !item.IsExpanded;
    }
}
