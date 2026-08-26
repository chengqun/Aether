using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文件树右键操作的共享实现（<see cref="FileTreeView"/> 文档页 + <see cref="KoTreeView"/> 知识对象页共用）。
/// 收拢两类树的右键命令背后共用的纯文件系统动作：内联输入/确认对话框、写剪贴板、在文件管理器中显示、
/// macOS 移入废纸篓、重命名/删除落盘。每个动作不依赖具体 VM——传入 <see cref="FileNode"/> + 父窗口即可。
/// 目标路径变更/增删由各 VM 的 <see cref="FileSystemWatcher"/> 自动刷树（无需手动通知）。
/// </summary>
public static class FileTreeOps
{
    // —— 内联输入对话框（取名用。简易内联构建，无额外 axaml）——

    /// <summary>弹输入框取名（重命名/新建）。null=取消。回车=确定。</summary>
    public static async Task<string?> PromptNameAsync(Window parent, string title, string message, string fieldLabel, string initial = "")
    {
        string? result = null;
        var dlg = new Window
        {
            Title = title,
            Width = 400, Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = initial, PlaceholderText = fieldLabel };
        root.Children.Add(box);
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 8) };
        var okBtn = new Button { Content = "确定", Padding = new Thickness(20, 8) };
        okBtn.Classes.Add("primary");
        btns.Children.Add(cancel);
        btns.Children.Add(okBtn);
        root.Children.Add(btns);
        dlg.Content = root;
        cancel.Click += (_, _) => dlg.Close();
        okBtn.Click += (_, _) => { result = box.Text; dlg.Close(); };
        box.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Enter) { result = box.Text; dlg.Close(); }
        };
        await dlg.ShowDialog(parent);
        return result;
    }

    /// <summary>简易确认对话框（内联构建）。返回 true=点了确认。</summary>
    public static async Task<bool> ConfirmAsync(Window parent, string title, string message, string confirmText)
    {
        var result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 400, Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var root = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 8) };
        var okBtn = new Button { Content = confirmText, Padding = new Thickness(20, 8) };
        okBtn.Classes.Add("primary");
        btns.Children.Add(cancel);
        btns.Children.Add(okBtn);
        root.Children.Add(btns);
        dlg.Content = root;
        cancel.Click += (_, _) => dlg.Close();
        okBtn.Click += (_, _) => { result = true; dlg.Close(); };
        await dlg.ShowDialog(parent);
        return result;
    }

    // —— 剪贴板 ——

    /// <summary>写剪贴板（前导/后随静默）。剪贴板不可用静默忽略。</summary>
    public static async Task CopyToClipboardAsync(Window parent, string text)
    {
        try
        {
            var clipboard = parent.Clipboard;
            if (clipboard is not null) await ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        catch { /* 剪贴板不可用 → 静默 */ }
    }

    // —— 在文件管理器中显示（macOS 对文件名用 `open -R` 选中高亮；其它打开父目录）——

    /// <summary>在系统文件管理器中定位该节点（文件夹→打开该文件夹；文件→打开父目录并在 macOS 选中高亮）。</summary>
    public static void RevealInFileManager(FileNode node)
    {
        var path = node.IsFolder ? node.Path : Path.GetDirectoryName(node.Path);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsMacOS() && !node.IsFolder)
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{node.Path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch { /* 打开失败静默，不影响主流程 */ }
    }

    // —— 删除（macOS 移入废纸篓可恢复，其它平台永久删除）——

    /// <summary>删除文件/文件夹。macOS → 移入 <c>~/.Trash</c>（重名追加序号）；其它平台 → 永久删除。</summary>
    public static void Delete(FileNode node)
    {
        if (OperatingSystem.IsMacOS()) MoveToTrash(node.Path);
        else if (node.IsFolder) Directory.Delete(node.Path, recursive: true);
        else File.Delete(node.Path);
    }

    /// <summary>macOS 移入废纸篓（~/.Trash）。目标重名时追加「 2」「 3」… 直到唯一，避免覆盖已有废纸篓条目。</summary>
    private static void MoveToTrash(string path)
    {
        var trash = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        Directory.CreateDirectory(trash);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = Path.Combine(trash, name);
        var n = 2;
        while (File.Exists(dest) || Directory.Exists(dest))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            dest = Path.Combine(trash, $"{stem} {n}{ext}");
            n++;
        }
        if (Directory.Exists(path)) Directory.Move(path, dest);
        else File.Move(path, dest);
    }
}
