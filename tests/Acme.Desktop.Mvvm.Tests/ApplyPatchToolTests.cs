using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.SystemTools.AI.Tools;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="ApplyPatchTool"/> 批量编辑 + 原子回滚测试。用临时文件，测完清理。</summary>
public class ApplyPatchToolTests
{
    private readonly ApplyPatchTool _tool = new();

    private static string TempFile(string? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "aether-test-" + Path.GetRandomFileName());
        if (content is not null) File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ApplyPatch_AllCreate_Succeeds()
    {
        var f1 = TempFile();
        var f2 = TempFile();
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f1, Operation = "create", Content = "hello" },
                new ApplyPatchTool.PatchEntry { FilePath = f2, Operation = "create", Content = "world" },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Equal(2, r.Succeeded);
            Assert.Equal(0, r.Failed);
            Assert.Equal("hello", await File.ReadAllTextAsync(f1));
            Assert.Equal("world", await File.ReadAllTextAsync(f2));
        }
        finally
        {
            if (File.Exists(f1)) File.Delete(f1);
            if (File.Exists(f2)) File.Delete(f2);
        }
    }

    [Fact]
    public async Task ApplyPatch_EditExisting_Succeeds()
    {
        var f = TempFile("line1\nline2\nline3");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry
                {
                    FilePath = f, Operation = "edit",
                    OldString = "line2", NewString = "LINE TWO",
                },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Equal("line1\nLINE TWO\nline3", await File.ReadAllTextAsync(f));
            Assert.Equal(1, r.Results[0].Count); // 1 替换
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_SecondPatchFails_RollsBackFirst()
    {
        var f1 = TempFile("original-1");
        var f2 = "non/existent/dir/deep/file.txt"; // create 会因路径非法或目录问题……改用不存在的 edit
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f1, Operation = "create", Content = "modified-1" },
                // 第二个 patch 失败：edit 一个不存在的文件
                new ApplyPatchTool.PatchEntry { FilePath = f2, Operation = "edit", OldString = "x", NewString = "y" },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.False(r.Success);
            Assert.Equal(1, r.Succeeded);
            Assert.Equal(1, r.Failed);
            Assert.True(r.RollbackComplete);
            // 回滚：f1 恢复成 original-1（create 被逆操作 = 写回 old）
            Assert.Equal("original-1", await File.ReadAllTextAsync(f1));
        }
        finally { if (File.Exists(f1)) File.Delete(f1); }
    }

    [Fact]
    public async Task ApplyPatch_NewFileRolledBack_IsDeleted()
    {
        var f1 = TempFile(); // 工具执行前不存在 → create 新建 → 回滚应删除
        File.Delete(f1);
        var f2Fail = "/this/path/does/not/exist/anywhere/file.txt";
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f1, Operation = "create", Content = "new" },
                new ApplyPatchTool.PatchEntry { FilePath = f2Fail, Operation = "edit", OldString = "x", NewString = "y" },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.False(r.Success);
            Assert.True(r.RollbackComplete);
            Assert.False(File.Exists(f1)); // 新建的文件被回滚删除
        }
        finally { if (File.Exists(f1)) File.Delete(f1); }
    }

    [Fact]
    public async Task ApplyPatch_RejectsDeleteInSafeEntry()
    {
        var f = TempFile("x");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f, Operation = "delete" },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.False(r.Success);
            Assert.Contains("不允许 delete", r.Results[0].Error);
            Assert.True(File.Exists(f)); // 未被删除
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatchWithDelete_DeletesFile()
    {
        var f = TempFile("to-delete");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f, Operation = "delete" },
            };

            var r = await _tool.ApplyPatchWithDelete(patches, CancellationToken.None);

            Assert.True(r.Success);
            Assert.False(File.Exists(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_LineEditReplace_Succeeds()
    {
        var f = TempFile("a\nb\nc\nd");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry
                {
                    FilePath = f, Operation = "lineEdit",
                    LineOperation = "replace", StartLine = 2, EndLine = 3, LineContent = "B\nC",
                },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success, r.Results.FirstOrDefault()?.Error ?? r.Error);
            // File.WriteAllLinesAsync 每行带换行（含末行）→ 尾部多一个 \n
            Assert.Equal("a\nB\nC\nd\n", await File.ReadAllTextAsync(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_EmptyPatches_ReturnsError()
    {
        var r = await _tool.ApplyPatch(System.Array.Empty<ApplyPatchTool.PatchEntry>(), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("不能为空", r.Error);
    }

    [Fact]
    public async Task ApplyPatch_MultiEditSameFile_AllApplied()
    {
        var f = TempFile("foo bar baz");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry { FilePath = f, Operation = "edit", OldString = "foo", NewString = "FOO" },
                new ApplyPatchTool.PatchEntry { FilePath = f, Operation = "edit", OldString = "bar", NewString = "BAR" },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Equal("FOO BAR baz", await File.ReadAllTextAsync(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_LineEditInsert_InsertsBeforeStartLine()
    {
        // StartLine 是 1-based（与 delete/replace 的 `start - 1` 同语义）——旧实现误把它当 0-based 索引
        // 传给 InsertRange，插到了「下一行」之前。回归守卫。
        var f = TempFile("a\nb\nc");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry
                {
                    FilePath = f, Operation = "lineEdit",
                    LineOperation = "insert", StartLine = 2, LineContent = "X",
                },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success, r.Results.FirstOrDefault()?.Error ?? r.Error);
            Assert.Equal("a\nX\nb\nc\n", await File.ReadAllTextAsync(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_LineEditInsert_AtStartLine1_Prepends()
    {
        var f = TempFile("a\nb");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry
                {
                    FilePath = f, Operation = "lineEdit",
                    LineOperation = "insert", StartLine = 1, LineContent = "head",
                },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success, r.Results.FirstOrDefault()?.Error ?? r.Error);
            Assert.Equal("head\na\nb\n", await File.ReadAllTextAsync(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task ApplyPatch_LineEditInsert_PastEndAppendsInsteadOfThrowing()
    {
        // StartLine = 总行数+1 是合法「追加到末尾」（校验上限 `total + 1` 允许），
        // 旧实现 `InsertRange(total, …)` 对 List<string> 合法——但配合 1-based 语义须为 `total`
        // 即 `start - 1`。同时保证多行 LineContent 拆分后整体落尾。
        var f = TempFile("a\nb\nc");
        try
        {
            var patches = new[]
            {
                new ApplyPatchTool.PatchEntry
                {
                    FilePath = f, Operation = "lineEdit",
                    LineOperation = "insert", StartLine = 4, LineContent = "tail1\ntail2",
                },
            };

            var r = await _tool.ApplyPatch(patches, CancellationToken.None);

            Assert.True(r.Success, r.Results.FirstOrDefault()?.Error ?? r.Error);
            Assert.Equal("a\nb\nc\ntail1\ntail2\n", await File.ReadAllTextAsync(f));
        }
        finally { if (File.Exists(f)) File.Delete(f); }
    }
}
