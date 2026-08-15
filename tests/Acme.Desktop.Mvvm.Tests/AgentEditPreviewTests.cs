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
}
