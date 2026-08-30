using System;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 通用「类型目录 → 文件卡片」视图（labs 页雨栏内嵌，点类型根目录时代替展开文件树）。
/// DataContext 绑 <see cref="FileCardsViewModel"/>。点卡片「→」→ 引发 <see cref="OpenFileRequested"/>，
/// 由外层 <see cref="KnowledgeObjectsView"/> 订阅并转调 <see cref="KnowledgeObjectsViewModel"/> 打开文件。
/// 只读列表——「新建」走左树该目录的右键菜单（已有）。
/// </summary>
public partial class FileCardsView : UserControl
{
    public FileCardsView()
    {
        InitializeComponent();
    }

    /// <summary>用户点卡片「打开」按钮，参数=要打开的文件绝对路径。</summary>
    public event Action<string>? OpenFileRequested;

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileCardItem item } && !string.IsNullOrWhiteSpace(item.Path))
            OpenFileRequested?.Invoke(item.Path);
    }
}
