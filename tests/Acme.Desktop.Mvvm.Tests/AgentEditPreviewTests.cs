using System.Collections.Generic;
using System.IO;
using Acme.Desktop.Mvvm.Git;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="AgentEditPreview"/> 的判断 + 取参测试（事后 diff/撤销用）。
/// 原 TryBuildDiff 事前预演已删（改为事后 call 快照 old + result 读 new），相关用例随之移除。</summary>
public class AgentEditPreviewTests
{
    private static Dict Input(params (string k, object? v)[] pairs)
    {
        var d = new Dict();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private sealed class Dict : Dictionary<string, object> { }

    [Fact]
    public void IsFileEditTool_RecognizesContentEditors()
    {
        Assert.True(AgentEditPreview.IsFileEditTool("EditFile"));
        Assert.True(AgentEditPreview.IsFileEditTool("WriteFile"));
        Assert.True(AgentEditPreview.IsFileEditTool("LineEditFile"));
    }

    [Fact]
    public void IsFileEditTool_RejectsNonContentTools()
    {
        Assert.False(AgentEditPreview.IsFileEditTool("DeleteFile"));
        Assert.False(AgentEditPreview.IsFileEditTool("MoveFile"));
        Assert.False(AgentEditPreview.IsFileEditTool("ExecuteShellCommand"));
        Assert.False(AgentEditPreview.IsFileEditTool(null));
    }

    [Fact]
    public void TryGetFilePath_ResolvesAbsolutePath()
    {
        var path = Path.GetTempFileName();
        try
        {
            var resolved = AgentEditPreview.TryGetFilePath(Input(("filePath", path)));
            Assert.Equal(Path.GetFullPath(path), resolved);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryGetFilePath_ReturnsNullWhenMissing()
    {
        Assert.Null(AgentEditPreview.TryGetFilePath(Input(("command", "ls"))));
        Assert.Null(AgentEditPreview.TryGetFilePath(null));
        Assert.Null(AgentEditPreview.TryGetFilePath(Input(("filePath", "   "))));
    }

    [Fact]
    public void IsApplyPatchTool_RecognizesBatchEditors()
    {
        Assert.True(AgentEditPreview.IsApplyPatchTool("ApplyPatch"));
        Assert.True(AgentEditPreview.IsApplyPatchTool("ApplyPatchWithDelete"));
        Assert.False(AgentEditPreview.IsApplyPatchTool("EditFile"));
        Assert.False(AgentEditPreview.IsApplyPatchTool(null));
    }

    [Fact]
    public void IsFileEditTool_DoesNotIncludeApplyPatch()
    {
        // ApplyPatch 走多文件路径（FileEdits），不走单文件 FileEdit
        Assert.False(AgentEditPreview.IsFileEditTool("ApplyPatch"));
        Assert.False(AgentEditPreview.IsFileEditTool("ApplyPatchWithDelete"));
    }

    [Fact]
    public void TryGetFilePaths_ParsesPatchesArrayFromJsonElement()
    {
        // 模拟 MEAI 反序列化路径：patches 是 JsonElement 数组
        var json = """
{"patches": [
  {"filePath": "/tmp/a.cs", "operation": "edit"},
  {"filePath": "/tmp/b.cs", "operation": "create"},
  {"filePath": "/tmp/a.cs", "operation": "edit"}
]}
""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // 取 patches 属性的值（数组），模拟 MEAI 把参数反序列化成 JsonElement
        var input = new Dict { ["patches"] = doc.RootElement.GetProperty("patches").Clone() };

        var paths = AgentEditPreview.TryGetFilePaths(input);

        Assert.Equal(2, paths.Count); // 去重后 a.cs 只一份
        Assert.Contains(Path.GetFullPath("/tmp/a.cs"), paths);
        Assert.Contains(Path.GetFullPath("/tmp/b.cs"), paths);
    }

    [Fact]
    public void TryGetFilePaths_ReturnsEmptyWhenNoPatches()
    {
        Assert.Empty(AgentEditPreview.TryGetFilePaths(null));
        Assert.Empty(AgentEditPreview.TryGetFilePaths(new Dict()));
        Assert.Empty(AgentEditPreview.TryGetFilePaths(Input(("filePath", "/tmp/x"))));
    }
}
