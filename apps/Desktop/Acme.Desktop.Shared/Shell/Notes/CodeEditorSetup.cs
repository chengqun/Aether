using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Acme.Desktop.Mvvm.Syntax;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 共享 AvaloniaEdit 编辑器设置：语法高亮（按语法 key / 文件扩展名）+ VSCode 2026 主题双配色（2026-light / 2026-dark）+
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

    /// <summary>编辑器主题色（当前行底 + 选中底/前景）。随 ActualThemeVariant 切——Loaded 与主题切换都调。
    /// 配色取自 VSCode 2026 内置主题（2026-light.json / 2026-dark.json）：当前行 #EAEAEA40/#242526、
    /// 选区 #0069CC40/#276782dd、选区前景 = editor.foreground。alpha 用 #AARRGGBB（Avalonia Color.Parse 格式）。</summary>
    public static void ApplyTheme(AvaloniaEdit.TextEditor editor)
    {
        try
        {
            var dark = IsDark(editor);
            var tv = editor.TextArea.TextView;
            tv.CurrentLineBackground = new SolidColorBrush(Color.Parse(dark ? "#242526" : "#40EAEAEA"));
            tv.CurrentLineBorder = null;
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse(dark ? "#DD276782" : "#400069CC"));
            editor.TextArea.SelectionForeground = new SolidColorBrush(Color.Parse(dark ? "#BBBEBF" : "#202020"));
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>文件扩展名（含点，大小写不敏感）→ 语法 key。转发 <see cref="SyntaxMap.SyntaxForExtension"/>（单一来源）。</summary>
    public static string? SyntaxForExtension(string? extension) => SyntaxMap.SyntaxForExtension(extension);

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
        // VSCode 2026 配色：Comment 灰 / String 蓝 / Keyword 红 / Key 蓝（与内置语言一致）
        var (comment, str, num, kw, key, doc) = dark
            ? ("#8b949e", "#a5d6ff", "#79c0ff", "#ff7b72", "#79c0ff", "#8b949e")
            : ("#6e7781", "#0a3069", "#0550ae", "#cf222e", "#0550ae", "#6e7781");
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

    /// <summary>把内置定义的颜色重映射为 VSCode 2026 主题配色（2026-light 浅底 / 2026-dark 深底）。
    /// 2026 是 GitHub 风格：关键字红、字符串蓝、函数紫、类型棕、标签绿、注释灰——区别于经典 Light+/Dark+（蓝关键字/红字符串/棕函数/青类型）。
    /// 深底用亮色 token 保对比度。色值取自 VSCode app bundle 内 2026-light.json / 2026-dark.json 的 tokenColors。</summary>
    private static void ApplyVscodeColors(IHighlightingDefinition def, bool dark)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            var n = (c.Name ?? "").ToUpperInvariant();
            string? hex = null;
            // 内置 MarkDown 定义的颜色名（Heading/BlockQuote/Link/Image/LineBreak）不匹配下方代码语言关键词桶，
            // 不处理则深色底留内置浅色（Maroon/DarkBlue/Blue/Green + LightGray 行背景）低对比或刺眼。按 GitHub 风格重映射。
            // Code/Emphasis/StrongEmphasis 无前景色（继承文本色），两主题皆正常，故不在内、跳过。
            // 不清 FontWeight/FontStyle（粗/斜体由定义自身管，区别于代码桶的「VSCode 关键字不加粗」清权）。
            if (n is "HEADING" or "BLOCKQUOTE" or "LINK" or "IMAGE" or "LINEBREAK")
            {
                hex = (n, dark) switch
                {
                    ("HEADING", true) => "#79c0ff", ("HEADING", false) => "#0550ae",
                    ("BLOCKQUOTE", true) => "#8b949e", ("BLOCKQUOTE", false) => "#6e7681",
                    ("LINK", true) => "#a5d6ff", ("LINK", false) => "#0a3069",
                    ("IMAGE", true) => "#7ee787", ("IMAGE", false) => "#116329",
                    ("LINEBREAK", true) => "#8b949e", ("LINEBREAK", false) => "#6e7681",
                    _ => null,
                };
                if (hex is not null)
                    c.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
                c.Background = null; // 清 LineBreak 的 LightGray 背景等（深色底刺眼）→ 仅留前景
                continue; // 不清 FontWeight/FontStyle（保留粗/斜体）
            }
            if (n.Contains("COMMENT") || n.Contains("PREPROCESSOR") || n.Contains("DOC")) hex = dark ? "#8b949e" : "#6e7781";
            else if (n.Contains("STRING") || n.Contains("CHAR")) hex = dark ? "#a5d6ff" : "#0a3069";
            else if (n.Contains("NUMBER") || n.Contains("DIGIT")) hex = dark ? "#79c0ff" : "#0550ae";
            else if (n.Contains("METHOD") || n.Contains("CALL") || n.Contains("FUNCTION")) hex = dark ? "#d2a8ff" : "#8250df";
            else if (n.Contains("TYPE") || n.Contains("CLASS")) hex = dark ? "#ffa657" : "#953800";
            else if (n.Contains("TAG") || n.Contains("ELEMENT")) hex = dark ? "#7ee787" : "#116329";
            else if (n.Contains("ATTRIBUT")) hex = dark ? "#79c0ff" : "#0550ae";
            else if (n.Contains("KEYWORD") || n.Contains("VISIBILITY") || n.Contains("MODIFIER")
                || n.Contains("REFERENCE") || n.Contains("VALUE") || n.Contains("EXCEPTION")
                || n.Contains("GOTO") || n.Contains("CONTEXT") || n.Contains("OPERATOR")
                || n.Contains("PARAMETER") || n.Contains("NAMESPACE") || n.Contains("GETSET")
                || n.Contains("TRUEFALSE") || n.Contains("SEMANTIC") || n.Contains("CHECKED")
                || n.Contains("UNSAFE") || n.Contains("DECLARATION")) hex = dark ? "#ff7b72" : "#cf222e";
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
