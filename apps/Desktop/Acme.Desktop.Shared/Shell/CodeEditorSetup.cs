using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 共享 AvaloniaEdit 编辑器设置：语法高亮（按语法 key / 文件扩展名）+ VSCode Light+/Dark+ 双配色 +
/// 当前行/选中主题色 + 代码折叠。从 <c>NotesView.axaml.cs</c> 抽出，<see cref="NotesView"/> 与
/// <see cref="DocumentsView"/> 共用——是「文档/代码编辑」面（未来 AI 编程）的可复用编辑器设施。
/// 全部静态、无状态：颜色在 <see cref="HighlightingManager"/> 缓存实例上原地改，主题是 app 级全窗一致故全局重染无冲突。
/// </summary>
internal static class CodeEditorSetup
{
    /// <summary>编辑器当前是否深色主题——读 <see cref="StyledElement.ActualThemeVariant"/>
    /// （WorkbenchWindow 设 Application.RequestedThemeVariant 后向下传播到编辑器）。</summary>
    public static bool IsDark(AvaloniaEdit.TextEditor editor)
        => editor.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>VSCode 质感：当前行高亮 + 选中色 + 搜索面板。仅 Loaded 调一次（避免重复装 SearchPanel）。
    /// 折叠管理器（<see cref="FoldingManager"/>）由调用方自行 Install 并持有字段——每编辑器一份。</summary>
    public static void ConfigureAppearance(AvaloniaEdit.TextEditor editor)
    {
        try
        {
            editor.Options.HighlightCurrentLine = true;
            SearchPanel.Install(editor);
            ApplyTheme(editor);
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>编辑器主题色（当前行底 + 选中底/前景）。随 ActualThemeVariant 切——Loaded 与主题切换都调。</summary>
    public static void ApplyTheme(AvaloniaEdit.TextEditor editor)
    {
        try
        {
            var dark = IsDark(editor);
            var tv = editor.TextArea.TextView;
            tv.CurrentLineBackground = new SolidColorBrush(Color.Parse(dark ? "#2a2b3e" : "#F0F0F4"));
            tv.CurrentLineBorder = null;
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse(dark ? "#264f78" : "#ADD6FF"));
            editor.TextArea.SelectionForeground = new SolidColorBrush(Color.Parse(dark ? "#cdd6f4" : "#1A1A1F"));
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>文件扩展名（含点，大小写不敏感）→ 语法 key。未知扩展名返回 null（纯文本，不高亮）。</summary>
    public static string? SyntaxForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".py" or ".pyw" => "python",
            ".md" or ".markdown" => "markdown",
            ".json" => "json",
            ".sql" => "sql",
            ".yaml" or ".yml" => "yaml",
            ".js" or ".mjs" or ".cjs" or ".ts" => "javascript",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".xml" or ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" => "xml",
            ".java" => "java",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".h" => "cpp",
            ".vb" => "vb",
            ".ps1" => "powershell",
            _ => null,
        };
    }

    /// <summary>把语法 key 映射到 AvaloniaEdit 内置定义名（<see cref="HighlightingManager.GetDefinition"/> 接受的名字）。
    /// yaml 走自定义内联 .xshd（AvaloniaEdit 无内置 YAML）。</summary>
    private static string? SyntaxToDefinitionName(string? syntax)
        => syntax switch
        {
            "markdown" => "MarkDown",
            "sql" => "TSQL",
            "python" => "Python",
            "javascript" => "JavaScript",
            "json" => "Json",
            "csharp" => "C#",
            "html" => "HTML",
            "css" => "CSS",
            "xml" => "XML",
            "java" => "Java",
            "cpp" => "C++",
            "vb" => "VB",
            "powershell" => "PowerShell",
            _ => null,
        };

    /// <summary>语法 key → 高亮定义：yaml 走自定义 .xshd，其余走内置定义 + VSCode 配色重映射。null = 纯文本。</summary>
    public static IHighlightingDefinition? GetHighlighting(string? syntax, bool dark)
    {
        if (string.IsNullOrEmpty(syntax)) return null;
        var s = syntax!.ToLowerInvariant();
        if (s == "yaml") return LoadYaml(dark);
        return Load(SyntaxToDefinitionName(s), dark);
    }

    /// <summary>设编辑器高亮（含主题切换时的 null-reset 强制重绘：缓存实例颜色原地改，设同引用不触发重绘）。</summary>
    public static void ApplyHighlighting(AvaloniaEdit.TextEditor editor, string? syntax)
    {
        var def = GetHighlighting(syntax, IsDark(editor));
        if (def is not null && ReferenceEquals(editor.SyntaxHighlighting, def))
        {
            editor.SyntaxHighlighting = null;
            editor.SyntaxHighlighting = def;
        }
        else
        {
            editor.SyntaxHighlighting = def;
        }
    }

    /// <summary>按语法类型生成折叠区域：大括号语言折叠 {}、SQL 折叠 ()、YAML 折叠 --- 文档段。manager 为 null 则跳过。</summary>
    public static void UpdateFoldings(FoldingManager? manager, string text, string? syntax)
    {
        if (manager is null) return;
        var kind = syntax?.ToLowerInvariant();
        var foldings = new List<NewFolding>();
        if (kind == "yaml")
            FoldYamlDocuments(text, foldings);
        else if (kind == "sql")
            FoldPairs(text, foldings, '(', ')');
        else
            FoldPairs(text, foldings, '{', '}'); // C 风格 + JSON
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        manager.UpdateFoldings(foldings, -1);
    }

    // —— 内部实现（原 NotesView.axaml.cs 私有静态方法原样搬迁）——

    private static IHighlightingDefinition? _yamlLight, _yamlDark;

    private static IHighlightingDefinition LoadYaml(bool dark)
        => dark ? (_yamlDark ??= BuildYaml(dark)) : (_yamlLight ??= BuildYaml(dark));

    private static IHighlightingDefinition BuildYaml(bool dark)
    {
        // Light+ : Comment 绿 / String 暗红 / Keyword 蓝 / Key 青
        // Dark+  : Comment 暗绿 / String 橙 / Key 浅蓝（深底高对比，暗红在深底不可读）
        var (comment, str, num, kw, key, doc) = dark
            ? ("#6A9955", "#CE9178", "#B5CEA8", "#569CD6", "#9CDCFE", "#6A9955")
            : ("#008000", "#A31515", "#098658", "#0000FF", "#267F99", "#808080");
        var xshd = $@"<SyntaxDefinition name=""YAML"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""{comment}""/>
  <Color name=""String"" foreground=""{str}""/>
  <Color name=""Number"" foreground=""{num}""/>
  <Color name=""Keyword"" foreground=""{kw}""/>
  <Color name=""Key"" foreground=""{key}""/>
  <Color name=""DocMarker"" foreground=""{doc}""/>
  <RuleSet>
    <Rule color=""DocMarker"">^---\s*$</Rule>
    <Rule color=""DocMarker"">^\.\.\.\s*$</Rule>
    <Rule color=""Comment"">\#.*</Rule>
    <Rule color=""Key"">^\s*[\w-]+(?=\s*:)</Rule>
    <Rule color=""Key"">\b[\w-]+(?=\s*:)</Rule>
    <Span color=""String""><Begin>&quot;</Begin><End>&quot;</End></Span>
    <Span color=""String""><Begin>'</Begin><End>'</End></Span>
    <Rule color=""Number"">\b\d+(\.\d+)?\b</Rule>
    <Keywords color=""Keyword"">
      <Word>true</Word><Word>false</Word><Word>null</Word>
      <Word>yes</Word><Word>no</Word><Word>on</Word><Word>off</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static IHighlightingDefinition? Load(string? name, bool dark)
    {
        if (name is null) return null;
        // GetDefinition 返回 HighlightingManager 缓存的同一实例——颜色在 ApplyVscodeColors 里原地改。
        // 主题是 app 级（全窗一致），故全局重染无冲突；切主题后 ApplyHighlighting 先置 null 再设回强制重绘。
        var def = HighlightingManager.Instance.GetDefinition(name);
        if (def is not null) ApplyVscodeColors(def, dark);
        return def;
    }

    /// <summary>把内置定义的颜色重映射为 VSCode 配色（Light+ 浅底 / Dark+ 深底）。深底用亮色语法 token 保对比度。</summary>
    private static void ApplyVscodeColors(IHighlightingDefinition def, bool dark)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            var n = (c.Name ?? "").ToUpperInvariant();
            string? hex = null;
            if (n.Contains("COMMENT") || n.Contains("PREPROCESSOR") || n.Contains("DOC")) hex = dark ? "#6A9955" : "#008000";
            else if (n.Contains("STRING") || n.Contains("CHAR")) hex = dark ? "#CE9178" : "#A31515";
            else if (n.Contains("NUMBER") || n.Contains("DIGIT")) hex = dark ? "#B5CEA8" : "#098658";
            else if (n.Contains("METHOD") || n.Contains("CALL") || n.Contains("FUNCTION")) hex = dark ? "#DCDCAA" : "#795E26";
            else if (n.Contains("TYPE") || n.Contains("CLASS")) hex = dark ? "#4EC9B0" : "#267F99";
            else if (n.Contains("TAG") || n.Contains("ELEMENT")) hex = dark ? "#569CD6" : "#800000";
            else if (n.Contains("ATTRIBUT")) hex = dark ? "#9CDCFE" : "#FF0000";
            else if (n.Contains("KEYWORD") || n.Contains("VISIBILITY") || n.Contains("MODIFIER")
                || n.Contains("REFERENCE") || n.Contains("VALUE") || n.Contains("EXCEPTION")
                || n.Contains("GOTO") || n.Contains("CONTEXT") || n.Contains("OPERATOR")
                || n.Contains("PARAMETER") || n.Contains("NAMESPACE") || n.Contains("GETSET")
                || n.Contains("TRUEFALSE") || n.Contains("SEMANTIC") || n.Contains("CHECKED")
                || n.Contains("UNSAFE") || n.Contains("DECLARATION")) hex = dark ? "#569CD6" : "#0000FF";
            if (hex is not null)
            {
                c.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
                c.FontWeight = null; // VSCode 关键字不加粗
            }
        }
    }

    /// <summary>配对字符折叠（{} 或 ()），超过 2 字符才折。</summary>
    private static void FoldPairs(string text, List<NewFolding> foldings, char open, char close)
    {
        var starts = new Stack<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == open) starts.Push(i);
            else if (text[i] == close && starts.Count > 0)
            {
                var start = starts.Pop();
                if (i - start > 2)
                    foldings.Add(new NewFolding(start, i + 1));
            }
        }
    }

    /// <summary>YAML 文档段折叠：--- 到下一个 ---/... 之间。</summary>
    private static void FoldYamlDocuments(string text, List<NewFolding> foldings)
    {
        var marks = new List<int>();
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                var line = text.AsSpan(lineStart, i - lineStart).Trim('\r').Trim();
                if (line is "---" or "...")
                    marks.Add(lineStart);
                lineStart = i + 1;
            }
        }
        for (var i = 0; i + 1 < marks.Count; i++)
        {
            if (marks[i + 1] - marks[i] > 4)
                foldings.Add(new NewFolding(marks[i], marks[i + 1]));
        }
    }
}
