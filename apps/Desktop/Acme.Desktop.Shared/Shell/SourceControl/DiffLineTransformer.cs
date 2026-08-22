using System.Collections.Generic;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Acme.Desktop.Shared.Shell;

/// <summary>diff 行底色着色器：按 1-based lineNumber→<see cref="DiffLineKind"/> 给 Added/Removed 行上底色（绿/红，半透明适配明暗）。
/// 装在 <c>editor.TextArea.TextView.LineTransformers</c>。Context/Blank 行不着色。仅 diff 视图用。</summary>
internal sealed class DiffLineTransformer : DocumentColorizingTransformer
{
    private readonly IReadOnlyDictionary<int, DiffLineKind> _kinds;
    private readonly IBrush _added;
    private readonly IBrush _removed;

    public DiffLineTransformer(IReadOnlyDictionary<int, DiffLineKind> kinds, bool dark)
    {
        _kinds = kinds;
        // 半透明底色，明暗两套保对比度。
        _added = new SolidColorBrush(Avalonia.Media.Color.Parse(dark ? "#1F4428" : "#D6F5DD"));
        _removed = new SolidColorBrush(Avalonia.Media.Color.Parse(dark ? "#4A2020" : "#FAD7D7"));
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_kinds.TryGetValue(line.LineNumber, out var kind)) return;
        var brush = kind switch
        {
            DiffLineKind.Added => _added,
            DiffLineKind.Removed => _removed,
            _ => null,
        };
        if (brush is null) return;
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetBackgroundBrush(brush));
    }
}
