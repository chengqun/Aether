using System.Collections.Generic;
using System.Linq;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Controls;
using AvaloniaEdit;

namespace Acme.Desktop.Shared.Shell;

/// <summary>左右并排 diff 视图 code-behind。DataContext = <see cref="DiffModel"/>。
/// 变化时填充双只读 <see cref="TextEditor"/>（左=OldLines / 右=NewLines）+ 语法高亮（复用 <see cref="CodeEditorSetup"/>）
/// + <see cref="DiffLineTransformer"/> 行底色 + 滚动同步。</summary>
public partial class DiffView : UserControl
{
    private bool _syncing;
    private bool _scrollWired;

    public DiffView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RenderDiff(DataContext as DiffModel);
    }

    private void RenderDiff(DiffModel? m)
    {
        if (m is null) return;
        if (m.IsBinary) return; // XAML 二进制占位已显

        // 编辑器文本取自行数组（保证行号与 kind map 严格对齐，避开尾换行偏差）。
        var leftText = string.Join("\n", m.OldLines.Select(l => l.Text));
        var rightText = string.Join("\n", m.NewLines.Select(l => l.Text));

        LeftEditor.Text = leftText;
        RightEditor.Text = rightText;

        CodeEditorSetup.ConfigureAppearance(LeftEditor);
        CodeEditorSetup.ConfigureAppearance(RightEditor);
        CodeEditorSetup.ApplyHighlighting(LeftEditor, m.SyntaxKey);
        CodeEditorSetup.ApplyHighlighting(RightEditor, m.SyntaxKey);

        InstallTransformer(LeftEditor, m.OldLines);
        InstallTransformer(RightEditor, m.NewLines);

        WireScrollSync();
    }

    /// <summary>构建 lineNumber→DiffLineKind map 并装/换 <see cref="DiffLineTransformer"/>。</summary>
    private void InstallTransformer(TextEditor editor, IReadOnlyList<DiffLine> lines)
    {
        var tv = editor.TextArea.TextView;
        // 清旧 diff transformer（主题/diff 切换时重装）。
        for (var i = tv.LineTransformers.Count - 1; i >= 0; i--)
            if (tv.LineTransformers[i] is DiffLineTransformer) tv.LineTransformers.RemoveAt(i);

        var map = new Dictionary<int, DiffLineKind>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            map[i + 1] = lines[i].Kind; // 1-based
        tv.LineTransformers.Add(new DiffLineTransformer(map, CodeEditorSetup.IsDark(editor)));
    }

    /// <summary>左右编辑器滚动同步（TextView.ScrollOffset 只读，经 TextEditor.ScrollTo*Offset 写）。</summary>
    private void WireScrollSync()
    {
        if (_scrollWired) return;
        _scrollWired = true;
        var lt = LeftEditor.TextArea.TextView;
        var rt = RightEditor.TextArea.TextView;
        lt.ScrollOffsetChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            RightEditor.ScrollToVerticalOffset(lt.ScrollOffset.Y);
            RightEditor.ScrollToHorizontalOffset(lt.ScrollOffset.X);
            _syncing = false;
        };
        rt.ScrollOffsetChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            LeftEditor.ScrollToVerticalOffset(rt.ScrollOffset.Y);
            LeftEditor.ScrollToHorizontalOffset(rt.ScrollOffset.X);
            _syncing = false;
        };
    }
}
