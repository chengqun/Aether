using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Syntax;
using Acme.Desktop.Shared.Theming;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>订阅「编辑器需重染」的三个触发源并执行 <paramref name="reapply"/>：
    /// ① <see cref="StyledElement.ActualThemeVariantChanged"/>（light↔dark variant 翻转）② <see cref="IThemeHost.ThemeChanged"/>
    /// （主题 JSON 换肤——含 variant 相同但 editor 段不同的情况，如 light→dream 同为 light 变体）
    /// ③ <see cref="ThemeLoader.EditorThemeApplied"/>（启动竞态补染——编辑器 Loaded 早于窗口 OnOpened 首次 Apply，
    /// 槽从 null→有值时由 ThemeLoader 主动通知，使首帧即用 JSON 配色而非内置兜底）。
    /// 供 4 个编辑器宿主（Notes/Documents/Knowledge/DiffView）共用，替代各自重复的 ActualThemeVariantChanged += 样板。</summary>
    public static void WireThemeReapply(AvaloniaEdit.TextEditor editor, Action reapply)
    {
        editor.ActualThemeVariantChanged += (_, _) => reapply();
        var host = DesktopApp.Services.GetService<MainViewModelBase>() as IThemeHost;
        if (host is not null)
            host.ThemeChanged += (_, _) => reapply();
        // 启动竞态补染：ThemeLoader.Apply 写入配色槽后主动通知（首次 Apply 在 OnOpened，编辑器已 Loaded 用了内置兜底）。
        ThemeLoader.EditorThemeApplied += (_, _) => reapply();
    }

    /// <summary>取当前皮肤 JSON 的编辑器配色槽（可能 null=未配 editor 段）。编辑器配色经皮肤系统可配——
    /// 用户在主题 JSON 的 "editor" 段声明色槽即覆盖内置 VSCode 2026；未声明则回落 <see cref="IsEditorDark"/> 的深/浅内置色。
    /// 线程安全：Apply 与读均在 UI 线程。</summary>
    private static EditorThemeDef? Palette => ThemeLoader.ActiveEditorTheme;

    /// <summary>编辑器「明暗」：优先按皮肤 variant（若配了 editor 段则同样驱动深/浅）；否则按控件 ActualThemeVariant。
    /// 注：editor 段色槽是「按当前 variant 全套覆盖」，不区分深/浅——由皮肤作者在 JSON 里给对应 variant 的值。</summary>
    private static bool IsEditorDark(AvaloniaEdit.TextEditor editor)
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
    /// 配色优先取皮肤 JSON 的 editor 段（ThemeLoader.ActiveEditorTheme），缺省回落 VSCode 2026 内置：
    /// 当前行 #EAEAEA40/#242526、选区 #0069CC40/#276782dd、选区前景 #202020/#BBBEBF。
    /// alpha 用 #AARRGGBB（Avalonia Color.Parse 格式）。</summary>
    public static void ApplyTheme(AvaloniaEdit.TextEditor editor)
    {
        try
        {
            var dark = IsDark(editor);
            var p = Palette;
            var tv = editor.TextArea.TextView;
            tv.CurrentLineBackground = new SolidColorBrush(Color.Parse(p?.CurrentLine ?? (dark ? "#242526" : "#40EAEAEA")));
            tv.CurrentLineBorder = null;
            // 编辑器前景（文字色）：皮肤提供（或主题 TextPrimary 派生）则设，否则回落内置暗/浅默认。
            editor.Foreground = new SolidColorBrush(Color.Parse(p?.Foreground ?? (dark ? "#e6e6e6" : "#1a1a1f")));
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse(p?.Selection ?? (dark ? "#DD276782" : "#400069CC")));
            editor.TextArea.SelectionForeground = new SolidColorBrush(Color.Parse(p?.SelectionForeground ?? (dark ? "#BBBEBF" : "#202020")));
            // 编辑器底：皮肤提供（或主题 ContentBg 派生）则覆盖；null=随原生 ThemeVariant。
            editor.TextArea.Background = p?.Background is { } bg ? new SolidColorBrush(Color.Parse(bg)) : null;
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
        // 编辑器配色优先皮肤 JSON editor 段，缺省回落 VSCode 2026：Comment 灰 / String 蓝 / Keyword 红 / Key 蓝（与内置语言一致）
        var p = Palette;
        var comment = p?.Comment ?? (dark ? "#8b949e" : "#6e7781");
        var str     = p?.String  ?? (dark ? "#a5d6ff" : "#0a3069");
        var num     = p?.Number  ?? (dark ? "#79c0ff" : "#0550ae");
        var kw      = p?.Keyword ?? (dark ? "#ff7b72" : "#cf222e");
        var key     = p?.Attribute ?? (dark ? "#79c0ff" : "#0550ae");
        var doc     = p?.Comment ?? (dark ? "#8b949e" : "#6e7781");
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

    /// <summary>把内置定义的颜色重映射为编辑器配色。优先皮肤 JSON 的 editor 段（逐槽覆盖），
    /// 缺省回落 VSCode 2026 主题配色（2026-light 浅底 / 2026-dark 深底）。
    /// 2026 是 GitHub 风格：关键字红、字符串蓝、函数紫、类型棕、标签绿、注释灰——区别于经典 Light+/Dark+（蓝关键字/红字符串/棕函数/青类型）。
    /// 深底用亮色 token 保对比度。色值取自 VSCode app bundle 内 2026-light.json / 2026-dark.json 的 tokenColors。</summary>
    private static void ApplyVscodeColors(IHighlightingDefinition def, bool dark)
    {
        var p = Palette;
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
                    ("HEADING", true) => p?.Heading ?? "#79c0ff", ("HEADING", false) => p?.Heading ?? "#0550ae",
                    ("BLOCKQUOTE", true) => p?.Comment ?? "#8b949e", ("BLOCKQUOTE", false) => p?.Comment ?? "#6e7681",
                    ("LINK", true) => p?.Link ?? "#a5d6ff", ("LINK", false) => p?.Link ?? "#0a3069",
                    ("IMAGE", true) => p?.Tag ?? "#7ee787", ("IMAGE", false) => p?.Tag ?? "#116329",
                    ("LINEBREAK", true) => p?.Comment ?? "#8b949e", ("LINEBREAK", false) => p?.Comment ?? "#6e7681",
                    _ => null,
                };
                if (hex is not null)
                    c.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
                c.Background = null; // 清 LineBreak 的 LightGray 背景等（深色底刺眼）→ 仅留前景
                continue; // 不清 FontWeight/FontStyle（保留粗/斜体）
            }
            if (n.Contains("COMMENT") || n.Contains("PREPROCESSOR") || n.Contains("DOC")) hex = p?.Comment ?? (dark ? "#8b949e" : "#6e7781");
            else if (n.Contains("STRING") || n.Contains("CHAR")) hex = p?.String ?? (dark ? "#a5d6ff" : "#0a3069");
            else if (n.Contains("NUMBER") || n.Contains("DIGIT")) hex = p?.Number ?? (dark ? "#79c0ff" : "#0550ae");
            else if (n.Contains("METHOD") || n.Contains("CALL") || n.Contains("FUNCTION")) hex = p?.Function ?? (dark ? "#d2a8ff" : "#8250df");
            else if (n.Contains("TYPE") || n.Contains("CLASS")) hex = p?.Type ?? (dark ? "#ffa657" : "#953800");
            else if (n.Contains("TAG") || n.Contains("ELEMENT")) hex = p?.Tag ?? (dark ? "#7ee787" : "#116329");
            else if (n.Contains("ATTRIBUT")) hex = p?.Attribute ?? (dark ? "#79c0ff" : "#0550ae");
            else if (n.Contains("KEYWORD") || n.Contains("VISIBILITY") || n.Contains("MODIFIER")
                || n.Contains("REFERENCE") || n.Contains("VALUE") || n.Contains("EXCEPTION")
                || n.Contains("GOTO") || n.Contains("CONTEXT") || n.Contains("OPERATOR")
                || n.Contains("PARAMETER") || n.Contains("NAMESPACE") || n.Contains("GETSET")
                || n.Contains("TRUEFALSE") || n.Contains("SEMANTIC") || n.Contains("CHECKED")
                || n.Contains("UNSAFE") || n.Contains("DECLARATION")) hex = p?.Keyword ?? (dark ? "#ff7b72" : "#cf222e");
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
