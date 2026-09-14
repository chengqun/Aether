using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.SystemTools.AI.Tools;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// GlobTool 通配符匹配回归测试——锁 `**/` 递归 glob 返 0 的修复
/// （旧实现只用 Path.GetFileName 对含 `/` 的正则匹配，`**/` 前的 `/` 永不命中）。
/// </summary>
public class GlobToolTests
{
    private static string CreateTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlobToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "foo"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        // 根
        File.WriteAllText(Path.Combine(root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "p");
        File.WriteAllText(Path.Combine(root, "foo", "foo.txt"), "foo");
        File.WriteAllText(Path.Combine(root, "foo", "sub.txt"), "sub");
        File.WriteAllText(Path.Combine(root, "src", "src.cs"), "s");
        File.WriteAllText(Path.Combine(root, "src", "a", "b", "deep.cs"), "d");
        File.WriteAllText(Path.Combine(root, "src", "a", "b", "deep.txt"), "d");
        // 忽略目录（bin/）不应被扫到
        File.WriteAllText(Path.Combine(root, "bin", "skip.txt"), "skip");
        return root;
    }

    [Fact]
    public async Task DoubleStar_MatchesRootAndNestedFiles()
    {
        var root = CreateTree();
        try
        {
            var result = await new GlobTool().Glob("**/*.txt", root, cancellationToken: CancellationToken.None);
            Assert.Null(result.Error);
            var rel = result.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "foo/foo.txt", "foo/sub.txt", "root.txt", "src/a/b/deep.txt" }, rel);
            Assert.DoesNotContain(rel, x => x.Contains("bin")); // 忽略目录不进结果
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Star_KeepsAnyDepthFilenameSemantics()
    {
        // 无 `/` 的 `*.ext` 保持既有语义：递归每层按文件名匹配（任意深度）。
        var root = CreateTree();
        try
        {
            var result = await new GlobTool().Glob("*.cs", root, cancellationToken: CancellationToken.None);
            Assert.Null(result.Error);
            var rel = result.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Program.cs", "src/a/b/deep.cs", "src/src.cs" }, rel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MidDoubleStar_RequiresPrefixDirectory()
    {
        var root = CreateTree();
        try
        {
            // src/x.cs 验证零个中间目录也命中；src/ 前缀必需 → 不命中根 Program.cs
            File.WriteAllText(Path.Combine(root, "src", "x.cs"), "x");
            var result = await new GlobTool().Glob("src/**/*.cs", root, cancellationToken: CancellationToken.None);
            Assert.Null(result.Error);
            var rel = result.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "src/a/b/deep.cs", "src/src.cs", "src/x.cs" }, rel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleSlash_IsDirectoryScoped()
    {
        var root = CreateTree();
        try
        {
            var result = await new GlobTool().Glob("foo/*.txt", root, cancellationToken: CancellationToken.None);
            Assert.Null(result.Error);
            var rel = result.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "foo/foo.txt", "foo/sub.txt" }, rel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QuestionMark_MatchesSingleCharWithinSegment()
    {
        var root = CreateTree();
        try
        {
            var result = await new GlobTool().Glob("foo/*.tx?", root, cancellationToken: CancellationToken.None);
            Assert.Null(result.Error);
            var rel = result.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "foo/foo.txt", "foo/sub.txt" }, rel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
