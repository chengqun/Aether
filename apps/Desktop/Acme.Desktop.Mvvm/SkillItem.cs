using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>技能卡片模型（专家页「技能」tab）：name/category/description + 正文 Content + 预览。
/// 照专家卡 <see cref="ExpertItem"/> 的卡片范式；<see cref="IsExpanded"/> 可变（点「查看正文」展开 SelectableMarkdownViewer 渲染 Content）。</summary>
public partial class SkillItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>skill 指令正文（Markdown），展开时由 SelectableMarkdownViewer 渲染（不展开不渲染，省资源）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>卡片预览文本：有 description 用 description，否则截 Content 前 140 字（无 description 时正文即唯一信息）。</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>是否展开正文预览（点「查看正文」切换，照专家卡「召唤」的就地交互范式）。</summary>
    [ObservableProperty] private bool _isExpanded;
}
