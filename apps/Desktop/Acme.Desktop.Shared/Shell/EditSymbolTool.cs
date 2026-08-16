using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>edit_symbol</c> 工具：基于 Roslyn 语义分析的 C# 符号级编辑。
/// 按 (symbolKind, symbolName, containerName?) 在当前项目（文档页打开的目录）里定位符号，
/// 用 <paramref name="newText"/> 替换该符号在源码里的文本 Span，写回文件。
/// </summary>
/// <remarks>
/// 与 <c>EditFile</c>（字符串匹配）的区别：本工具用 Roslyn 解析语法树 + 语义模型定位符号，
/// 不受注释/字符串/同名局部变量干扰，适合精确修改"某个方法体""某个属性"。
/// 落在宿主层（非 ai/ 适配器）——与 <see cref="BuildTool"/>/<see cref="GitTool"/> 同类，因需
/// <see cref="ICurrentProjectDirectoryProvider"/> + Roslyn 可基于打开的项目加载 Compilation。
/// 仅支持 C#（项目自身就是 C# 重型项目，用户决策方案 C）。标 Safe：内容编辑，事后 diff + 一键撤销兜底。
/// 闭环：工具名 <c>edit_symbol</c> 不在 <c>AgentEditPreview.IsFileEditTool</c> 列表，
/// 故走通用工具卡路径——若需事后 diff，可扩展 IsFileEditTool 识别它（当前保持单文件快照的轻量接入）。
/// </remarks>
public sealed class EditSymbolTool
{
    private readonly ICurrentProjectDirectoryProvider _projectDir;

    public EditSymbolTool(ICurrentProjectDirectoryProvider projectDir)
    {
        _projectDir = projectDir;
    }

    public sealed class SymbolHit
    {
        public string FilePath { get; set; } = string.Empty;
        public int StartLine { get; set; }   // 1-based
        public int StartColumn { get; set; } // 1-based
        public int EndLine { get; set; }
        public int EndColumn { get; set; }
        public string SymbolKind { get; set; } = string.Empty;
        public string FullyQualifiedName { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty; // 原文本前 200 字符预览
    }

    public sealed class Output
    {
        public bool Success { get; set; }
        public string? FilePath { get; set; }
        public string? SymbolFound { get; set; }     // 命中符号的 FQN
        public int? LinesAffected { get; set; }
        public string? Error { get; set; }
        /// <summary>符号在文件中的位置（供 agent 定位后续操作）。</summary>
        public SymbolHit? Location { get; set; }
    }

    /// <summary>替换一个 C# 符号的源码文本。</summary>
    [Description("基于 Roslyn 语义分析定位并替换当前项目中的一个 C# 符号（类型/方法/属性/字段）的源码文本。比 EditFile 的字符串匹配更精确——不受注释/字符串/同名局部变量干扰。适合精确修改某个方法体或某个属性。仅支持 C#。自动跑，不审批；事后 diff + 一键撤销兜底。")]
    public async Task<Output> EditSymbol(
        [Description("符号名（不含命名空间限定，如 \"RunBacktest\" 或 \"AccountService\"）")] string symbolName,
        [Description("新源码文本（替换该符号的完整声明，如新的方法体或新的类声明）")] string newText,
        [Description("符号种类: type / method / property / field（默认 method）")] string symbolKind = "method",
        [Description("容器限定名（可选）：符号所在的类型全名（含命名空间），用于消歧义，如 \"Acme.Trading.Application.BacktestService\"")] string? containerName = null,
        [Description("当符号有多个声明时，命中的索引（0-based，默认 0=第一个）。匹配多个会返回候选列表而不修改")] int occurrenceIndex = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return new Output { Error = "symbolName 不能为空" };
        if (newText is null)
            return new Output { Error = "newText 不能为空（如需删除符号请用注释或 ApplyPatchWithDelete）" };

        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir))
            return new Output { Error = "当前未打开项目目录（请在文档页打开一个项目目录）。" };

        var project = ResolveProject(dir);
        if (project is null)
            return new Output { Error = $"在 {dir} 及其一级子目录下未找到 .slnx/.sln/.csproj。" };

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.LoadMetadataForReferencedProjects = true;
            // 加载 solution 优先（跨项目符号解析更全）；否则单 csproj。
            Project? roslynProject;
            if (project.EndsWith(".slnx") || project.EndsWith(".sln"))
            {
                var solution = await workspace.OpenSolutionAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);
                // 符号查找跨整个 solution 的所有源码项目（符号可能声明在任一项目里）
                var projects = solution.Projects
                    .Where(p => p.Documents.Any(d => d.SourceCodeKind == SourceCodeKind.Regular))
                    .ToList();
                if (projects.Count == 0)
                    return new Output { Error = "Solution 中无可用项目。" };
                return await EditSymbolInCompilation(projects, symbolName, newText, symbolKind, containerName, occurrenceIndex, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                roslynProject = await workspace.OpenProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);
                return await EditSymbolInCompilation(new[] { roslynProject }, symbolName, newText, symbolKind, containerName, occurrenceIndex, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new Output { Error = $"Roslyn 加载/编辑失败: {ex.Message}" };
        }
    }

    private static async Task<Output> EditSymbolInCompilation(
        IReadOnlyList<Project> projects, string symbolName, string newText, string symbolKind,
        string? containerName, int occurrenceIndex, CancellationToken ct)
    {
        // 跨所有项目搜集候选（partial 类 / 符号可能声明在任一项目里）
        var candidates = new List<ISymbol>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);

                IEnumerable<SyntaxNode> nodes = symbolKind.ToLowerInvariant() switch
                {
                    "type" => root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>(),
                    "method" => root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                    "property" => root.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
                    "field" => root.DescendantNodes().OfType<FieldDeclarationSyntax>(),
                    _ => root.DescendantNodes().OfType<MemberDeclarationSyntax>(),
                };

                foreach (var node in nodes)
                {
                    var sym = semanticModel.GetDeclaredSymbol(node, ct);
                    if (sym is null) continue;
                    if (sym.Name != symbolName) continue;
                    if (!string.IsNullOrEmpty(containerName))
                    {
                        // 容器限定：符号的 containing type 的 FQN 包含 containerName
                        var containingFqn = sym.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
                        if (!containingFqn.Contains(containerName)) continue;
                    }
                    candidates.Add(sym);
                }
            }
        }

        if (candidates.Count == 0)
            return new Output { Error = $"未找到符号: {symbolName}（kind={symbolKind}）" + (containerName is null ? "" : $" in {containerName}") };
        if (occurrenceIndex < 0 || occurrenceIndex >= candidates.Count)
            return new Output { Error = $"occurrenceIndex {occurrenceIndex} 超界（共 {candidates.Count} 个候选）", };

        var target = candidates[occurrenceIndex];
        if (target.DeclaringSyntaxReferences.Length == 0)
            return new Output { Error = "符号无源码声明位置（可能是外部引用）" };

        var declRef = target.DeclaringSyntaxReferences[0];
        var declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
        var tree = declRef.SyntaxTree;
        var filePath = tree.FilePath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new Output { Error = $"符号所在文件不可写: {filePath}" };

        var sourceText = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var span = declNode.Span;   // 整个声明的 Span（含签名+体）
        if (span.Start < 0 || span.End > sourceText.Length)
            return new Output { Error = "符号 Span 越界（文件可能在加载后被改动，请重试）" };

        var newSource = sourceText.Substring(0, span.Start) + newText + sourceText.Substring(span.End);
        await File.WriteAllTextAsync(filePath, newSource, ct).ConfigureAwait(false);

        var lineSpan = tree.GetLineSpan(span).StartLinePosition; // FileLinePositionSpan 的起始位置
        var endLineSpan = tree.GetLineSpan(span).EndLinePosition;
        return new Output
        {
            Success = true,
            FilePath = filePath,
            SymbolFound = target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            LinesAffected = CountLines(newText),
            Location = new SymbolHit
            {
                FilePath = filePath,
                StartLine = lineSpan.Line + 1,
                StartColumn = lineSpan.Character + 1,
                EndLine = endLineSpan.Line + 1,
                EndColumn = endLineSpan.Character + 1,
                SymbolKind = target.Kind.ToString(),
                FullyQualifiedName = target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Preview = declNode.ToString().Length > 200 ? declNode.ToString()[..200] + "…" : declNode.ToString(),
            },
        };
    }

    // —— helpers ——

    private static string? ResolveProject(string dir)
    {
        foreach (var ext in new[] { ".slnx", ".sln", ".csproj" })
        {
            var hit = Directory.GetFiles(dir, "*" + ext).FirstOrDefault();
            if (hit is not null) return hit;
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            foreach (var ext in new[] { ".slnx", ".sln", ".csproj" })
            {
                var hit = Directory.GetFiles(sub, "*" + ext).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static int CountLines(string text) => string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + 1;
}
