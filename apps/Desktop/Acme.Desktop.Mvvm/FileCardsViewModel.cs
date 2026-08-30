using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「类型目录 → 资产卡片」VM（labs 页树点类型根目录时代替展开文件树，右栏平铺该类型资产的卡片）。
/// 纯展示容器——卡片数据（name/description/path）由 <see cref="KnowledgeObjectsViewModel"/> 按类型解析好后
/// 经 <see cref="LoadAsync(IReadOnlyList{FileCardItem}, string)"/> 注入（它持有各 parser 依赖 + labs 根路径）。
/// 供无专门列表 VM 的类型代理卡（agent/workflow/rule/coderule/knowledge/strategy 家族）复用。
/// </summary>
public partial class FileCardsViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;

    /// <summary>卡片列表（name/description/path）。</summary>
    public ObservableCollection<FileCardItem> Items { get; } = new();

    /// <summary>类型显示名（View 标题用）。</summary>
    [ObservableProperty] private string _title = string.Empty;

    public FileCardsViewModel(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>注入预解析的卡片列表并刷新（在 UI 线程 clear+add）。<paramref name="title"/> 为类型显示名。</summary>
    public async Task LoadAsync(IReadOnlyList<FileCardItem> items, string title)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            Title = title;
            Items.Clear();
            foreach (var i in items) Items.Add(i);
        });
    }

    [RelayCommand]
    private void Refresh() { /* 刷新由 KnowledgeObjectsViewModel 重新 BuildFileCardsAsync 触发，这里空实现保命令存在 */ }
}

/// <summary>资产卡片模型：名称 + 描述（有则显示）+ 落盘绝对路径（点卡片 → 打开文件）。</summary>
public partial class FileCardItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    /// <summary>描述/副标题（front-matter description / knowledge kind；无则空 → View 隐藏）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>是否带描述（驱动 View 副标题行可见性）。</summary>
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>类别徽标（agent/rule/workflow 的 category；无则空 → View 隐藏）。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>是否带类别徽标（驱动 View 徽标可见性）。</summary>
    public bool HasCategory => !string.IsNullOrEmpty(Category);

    public string Path { get; init; } = string.Empty;
}
