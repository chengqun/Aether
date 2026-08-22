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
    private bool _configured;       // ConfigureAppearance（含 SearchPanel.Install）只装一次
    private string? _lastSyntax;    // 仅语法变化时才重设高亮——避免每次点击同语法文件都强制重绘（闪烁根因）

    public DiffView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RenderDiff(DataContext as DiffModel);
        LeftEditor.ActualThemeVariantChanged += (_, _) => ReapplyTheme();
        RightEditor.ActualThemeVariantChanged += (_, _) => ReapplyTheme();
    }

    private void RenderDiff(DiffModel? m)
    {
        if (m is null || m.IsBinary) return; // null=隐藏态不清理（DiffView 随 HasActiveDiff 隐藏）；二进制 XAML 占位已显

        // 编辑器文本取自行数组（保证行号与 kind map 严格对齐，避开尾换行偏差）。
        var leftText = string.Join("\n", m.OldLines.Select(l => l.Text));
        var rightText = string.Join("\n", m.NewLines.Select(l => l.Text));

        // 外观（SearchPanel 等）只装一次；高亮仅语法变化时设——同语法文件切换不再触发 null-reset 强制重绘。
        if (!_configured)
        {
            CodeEditorSetup.ConfigureAppearance(LeftEditor);
            CodeEditorSetup.ConfigureAppearance(RightEditor);
            _configured = true;
        }
        if (_lastSyntax != m.SyntaxKey)
        {
            CodeEditorSetup.ApplyHighlighting(LeftEditor, m.SyntaxKey);
            CodeEditorSetup.ApplyHighlighting(RightEditor, m.SyntaxKey);
            _lastSyntax = m.SyntaxKey;
        }

        // diff 行底色 transformer 先装、再设文本——新文本首帧即按行 kind 正确着色，无纯文本中间帧。
        InstallTransformer(LeftEditor, m.OldLines);
        InstallTransformer(RightEditor, m.NewLines);

        LeftEditor.Text = leftText;
        RightEditor.Text = rightText;

        WireScrollSync();
    }

    /// <summary>主题切换：重染当前行/选区色 + 强制重高亮（null-reset）+ 重装 diff transformer。不重设文本，无闪烁。</summary>
    private void ReapplyTheme()
    {
        var m = DataContext as DiffModel;
        if (m is null || m.IsBinary) return;
        CodeEditorSetup.ApplyTheme(LeftEditor);
        CodeEditorSetup.ApplyTheme(RightEditor);
        CodeEditorSetup.ApplyHighlighting(LeftEditor, m.SyntaxKey);
        CodeEditorSetup.ApplyHighlighting(RightEditor, m.SyntaxKey);
        InstallTransformer(LeftEditor, m.OldLines);
        InstallTransformer(RightEditor, m.NewLines);
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
